using System;
using RbMcp.Http;
using RbMcp.Threading;
using RbMcp.Tools;
using ff14bot.AClasses;
using ff14bot.Helpers;

namespace RbMcp;

/// <summary>
/// RebornBuddy plugin host.
///
/// The plugin itself is deliberately thin: lifecycle, the pulse hook that lets
/// <see cref="GameThread"/> drain safely, and one button. Everything interesting is either a
/// registered tool or a snippet compiled at request time, so day-to-day iteration does not
/// require restarting RebornBuddy.
/// </summary>
public class Plugin : BotPlugin
{
    internal static readonly Version PluginVersion = new(0, 1, 0);

    private static BridgeServer _server;

    public override string Name => "RbMcp";
    public override string Author => "cheesegoldfish";
    public override Version Version => PluginVersion;

    public override string Description =>
        "Developer tool. Exposes RebornBuddy game data and live state to MCP clients over a " +
        "loopback HTTP API, plus arbitrary C# against the running process.";

    public override bool WantButton => true;

    /// <summary>
    /// Toggles observation recording. That is the only switch with no other UI, and it is
    /// the one worth having in reach: the ledger it fills records other players' character
    /// names to disk, so being able to see its state at a glance and kill it in one click
    /// matters more than saving a settings-file edit.
    /// </summary>
    public override string ButtonText =>
        BridgeSettings.Instance.RecordObservations
            ? "Recording observations - click to stop"
            : "Observations off - click to record";

    public override void OnInitialize()
    {
        ToolRegistry.RegisterAll();
    }

    public override void OnEnabled()
    {
        GameThread.Start();

        // Aura history only exists if something is sampling continuously, so wire it up
        // before the server starts accepting requests for it.
        Tracking.AuraTracker.Reset();
        GameThread.PeriodicWork = () =>
        {
            Tracking.AuraTracker.Sample();
            Tracking.WorldTracker.Sample();
        };

        _server = new BridgeServer();
        if (BridgeSettings.Instance.AutoStart)
            _server.Start();
    }

    public override void OnDisabled() => Shutdown();

    public override void OnShutdown() => Shutdown();

    /// <summary>
    /// Called on the botbase pulse thread. This is the only moment we can safely touch RB
    /// state while a bot is running, so queued work gets drained here.
    /// </summary>
    public override void OnPulse() => GameThread.DrainFromPulse();

    public override void OnButtonPress()
    {
        var settings = BridgeSettings.Instance;
        settings.RecordObservations = !settings.RecordObservations;
        settings.Save();

        Logging.Write(settings.RecordObservations
            ? "[RbMcp] Recording observations. Nearby players' character names are being written to disk."
            : "[RbMcp] Observation recording stopped.");
    }

    private static void Shutdown()
    {
        try { _server?.Stop(); }
        catch (Exception ex) { Logging.Write($"[RbMcp] Error stopping server: {ex.Message}"); }

        _server = null;

        // The observation ledger only flushes every thirty seconds, so an unload without
        // this loses whatever was catalogued since the last write.
        try { Tracking.WorldTracker.Flush(force: true); }
        catch (Exception ex) { Logging.Write($"[RbMcp] Error flushing observations: {ex.Message}"); }

        // Detach before stopping, or a hot reload leaves the old assembly's sampler
        // wired to a GameThread that is on its way down.
        GameThread.PeriodicWork = null;
        GameThread.Stop();
    }
}
