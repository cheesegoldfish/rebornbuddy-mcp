using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ff14bot.AClasses;
using ff14bot.Helpers;

namespace RbMcpLoader;

/// <summary>
/// Shim that gets RbMcp.dll in front of RebornBuddy's plugin loader, and
/// hot-reloads it when the file changes.
///
/// RB discovers plugins by compiling the .cs files it finds under Plugins\, not by
/// scanning for DLLs. A folder containing only a DLL is never looked at - the plugin
/// simply does not appear, with nothing logged to explain why. Every prebuilt plugin in
/// the ecosystem (Panda Farmer, Magitek) therefore ships a small source loader like this
/// one alongside its assembly.
///
/// Compiling the real plugin with the .NET SDK rather than shipping its source also
/// sidesteps a second problem: RB's compiler only references assemblies already loaded in
/// its process, so source that needs System.Text.Json or System.Net.HttpListener may not
/// build there even though both resolve fine at runtime from the shared framework.
///
/// This file must stay compilable against RB's own compiler, so it depends on nothing
/// beyond ff14bot and the BCL.
/// </summary>
public class RbMcpLoader : BotPlugin
{
    private const string AssemblyFileName = "RbMcp.dll";
    private const string TypeName = "RbMcp.Plugin";

    /// <summary>A single save fires several FileSystemWatcher events; collapse them.</summary>
    private const int DebounceMs = 750;

    /// <summary>The listener needs a moment to release the port before the new instance binds it.</summary>
    private const int ShutdownGraceMs = 250;

    private readonly object _swapLock = new object();

    private BotPlugin _inner;
    private FileSystemWatcher _watcher;
    private string _dllPath;
    private string _loadError;
    private bool _enabled;
    private DateTime _lastReload = DateTime.MinValue;

    public RbMcpLoader()
    {
        _dllPath = ResolveDllPath();
        Load();
        StartWatching();
    }

    private static string ResolveDllPath()
    {
        // RB compiles this file into a temp assembly whose Location may be empty, so fall
        // back to the well-known plugin folder.
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, AssemblyFileName)))
            dir = Path.Combine(ff14bot.Settings.GlobalSettings.Instance.PluginsPath, "RbMcp");

        return Path.Combine(dir, AssemblyFileName);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_dllPath))
            {
                _loadError = $"{AssemblyFileName} not found at {_dllPath}. Run scripts\\deploy.ps1.";
                Logging.Write($"[RbMcp] {_loadError}");
                return;
            }

            // Load from bytes rather than LoadFrom so the file is not locked - that is what
            // makes both rebuilding while RB runs and hot reload possible at all. The pdb
            // comes along purely so exceptions carry line numbers.
            var dllBytes = ReadWithRetry(_dllPath);
            var pdbPath = Path.ChangeExtension(_dllPath, ".pdb");

            byte[] pdbBytes = null;
            if (File.Exists(pdbPath))
            {
                try { pdbBytes = ReadWithRetry(pdbPath); }
                catch { /* symbols are a nicety, not a requirement */ }
            }

            var assembly = pdbBytes != null
                ? Assembly.Load(dllBytes, pdbBytes)
                : Assembly.Load(dllBytes);

            var type = assembly.GetType(TypeName);
            if (type == null)
            {
                _loadError = $"{TypeName} not found in the assembly.";
                Logging.Write($"[RbMcp] {_loadError}");
                return;
            }

            // BotPlugin identity comes from RebornBuddy.dll, which the loader and the
            // loaded assembly share, so forwarding needs no reflection past construction.
            _inner = (BotPlugin)Activator.CreateInstance(type);
            _loadError = null;
        }
        catch (ReflectionTypeLoadException ex)
        {
            // The failure that actually happens in practice: a reference RB cannot
            // resolve. Name it, because RB's own loader would swallow this silently.
            _loadError = ex.Message;
            Logging.Write($"[RbMcp] Load failed: {ex.Message}");
            foreach (var inner in ex.LoaderExceptions)
                Logging.Write($"[RbMcp]   {inner?.Message}");
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            Logging.Write($"[RbMcp] Load failed: {ex}");
        }
    }

    /// <summary>
    /// A build writes the DLL in pieces, so an event can arrive while the file is still
    /// locked or truncated. Retry briefly rather than failing the reload.
    /// </summary>
    private static byte[] ReadWithRetry(string path)
    {
        Exception last = null;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException ex) { last = ex; Thread.Sleep(100); }
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(100); }
        }

        throw last ?? new IOException($"Could not read {path}");
    }

    private void StartWatching()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dllPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, AssemblyFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnAssemblyChanged;
            _watcher.Created += OnAssemblyChanged;

            Logging.Write("[RbMcp] Hot-reload enabled. Rebuild and the plugin swaps itself in.");
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not start hot-reload watcher: {ex.Message}");
        }
    }

    private void OnAssemblyChanged(object sender, FileSystemEventArgs e)
    {
        lock (_swapLock)
        {
            if ((DateTime.UtcNow - _lastReload).TotalMilliseconds < DebounceMs) return;
            _lastReload = DateTime.UtcNow;
        }

        // Off the watcher thread: reloading blocks, and the old instance's HTTP listener
        // has to fully release port 8787 before the new one tries to bind it.
        ThreadPool.QueueUserWorkItem(_ => Reload());
    }

    private void Reload()
    {
        lock (_swapLock)
        {
            try
            {
                Logging.Write("[RbMcp] Change detected, reloading...");

                var wasEnabled = _enabled;

                // Tear the old instance down completely first. Its statics own a bound
                // HttpListener and a worker thread; the replacement lives in a freshly
                // loaded assembly with its own statics and would collide on the port.
                var old = _inner;
                _inner = null;

                if (old != null)
                {
                    try
                    {
                        if (wasEnabled) old.OnDisabled();
                        old.OnShutdown();
                    }
                    catch (Exception ex)
                    {
                        Logging.Write($"[RbMcp] Error shutting down previous instance: {ex.Message}");
                    }
                }

                Thread.Sleep(ShutdownGraceMs);

                Load();

                if (_inner == null)
                {
                    Logging.Write($"[RbMcp] Reload failed: {_loadError}");
                    return;
                }

                _inner.OnInitialize();
                if (wasEnabled) _inner.OnEnabled();

                Logging.Write($"[RbMcp] Reloaded v{_inner.Version}.");
            }
            catch (Exception ex)
            {
                Logging.Write($"[RbMcp] Reload failed: {ex}");
            }
        }
    }

    public override string Name => "RbMcp";
    public override string Author => "cheesegoldfish";
    public override Version Version => _inner?.Version ?? new Version(0, 0, 0);

    public override string Description => _inner?.Description ?? $"Failed to load: {_loadError}";

    public override bool WantButton => _inner?.WantButton ?? false;
    public override string ButtonText => _inner?.ButtonText ?? "Not loaded";

    public override void OnInitialize() => _inner?.OnInitialize();

    public override void OnEnabled()
    {
        _enabled = true;
        _inner?.OnEnabled();
    }

    public override void OnDisabled()
    {
        _enabled = false;
        _inner?.OnDisabled();
    }

    public override void OnShutdown()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch { /* shutting down */ }

        _inner?.OnShutdown();
    }

    public override void OnPulse() => _inner?.OnPulse();
    public override void OnButtonPress() => _inner?.OnButtonPress();
}
