using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RbMcp.Threading;
using RbMcp.Tools;
using ff14bot.Helpers;

namespace RbMcp.Http;

/// <summary>
/// Loopback HTTP front end. Deliberately plain JSON over three routes rather than MCP:
/// the MCP protocol lives in a shim process outside the game, so protocol churn never
/// costs an RB restart, and this API stays usable from curl or any script.
///
///   GET  /health  - liveness plus whether a character is actually logged in (no token)
///   GET  /tools   - tool catalogue, shaped as MCP tools/list entries
///   POST /rpc     - {"tool": "name", "args": {...}} -> {"ok": true, "result": ...}
///
/// Every request passes <see cref="Guard"/> first. Read that file before loosening anything
/// here - the checks look paranoid for a loopback service and are not.
/// </summary>
internal sealed class BridgeServer
{
    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private Task _acceptLoop;

    public bool IsRunning { get; private set; }
    public string Prefix { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        var settings = BridgeSettings.Instance;
        Prefix = $"http://{settings.Host}:{settings.Port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Error 5 is the classic one: HTTP.SYS wants either an elevated process or a
            // URL ACL reservation. RB usually runs elevated to read game memory, so this
            // mostly bites when it does not.
            Logging.Write($"[RbMcp] Could not bind {Prefix}: {ex.Message}");
            if (ex.ErrorCode == 5)
            {
                Logging.Write("[RbMcp] Access denied. Either run RebornBuddy as administrator, or reserve the URL once:");
                Logging.Write($"[RbMcp]   netsh http add urlacl url={Prefix} user=%USERNAME%");
            }

            _listener = null;
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));

        Logging.Write($"[RbMcp] Listening on {Prefix} ({ToolRegistry.Count} tools)");

        // Materialise the token now rather than on first request, so the file exists before
        // anyone goes looking for it during setup.
        if (settings.RequireAuthToken)
        {
            _ = AuthToken.Value;
            Logging.Write($"[RbMcp] Auth token: {AuthToken.FilePath}");
        }
        else
        {
            Logging.Write("[RbMcp] RequireAuthToken is false. Any local process can drive this bridge.");
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try { _cts?.Cancel(); } catch { /* shutting down */ }
        try { _listener?.Stop(); } catch { /* shutting down */ }
        try { _listener?.Close(); } catch { /* shutting down */ }

        _listener = null;
        Logging.Write("[RbMcp] Stopped.");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested || !IsRunning)
            {
                return; // Stop() pulled the listener out from under us; expected.
            }
            catch (Exception ex)
            {
                Logging.Write($"[RbMcp] Accept failed: {ex.Message}");
                continue;
            }

            // Handle off the accept loop so one slow tool cannot block the next request.
            _ = Task.Run(() => Handle(context), token);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? string.Empty;

            // Never answer a preflight. A browser that cannot preflight cannot send a
            // non-simple request, which is half of why /rpc demands application/json.
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Respond(context, 405, new { ok = false, error = "OPTIONS is not supported. This endpoint is not for browsers." });
                return;
            }

            var guard = Guard.Check(context.Request, path);
            if (!guard.Allowed)
            {
                if (BridgeSettings.Instance.VerboseLogging)
                    Logging.Write($"[RbMcp] Refused {context.Request.HttpMethod} {path}: {guard.Reason}");

                Respond(context, guard.Status, new { ok = false, error = guard.Reason });
                return;
            }

            switch (path)
            {
                case "":
                case "/health":
                    Respond(context, 200, new
                    {
                        ok = true,
                        service = "RbMcp",
                        version = Plugin.PluginVersion.ToString(),
                        tools = ToolRegistry.Count,
                        authRequired = BridgeSettings.Instance.RequireAuthToken,
                        botRunning = ff14bot.TreeRoot.IsRunning,
                        // The distinction that matters to a caller: the bridge can be up
                        // while the game is at the title screen, and then every tier-2
                        // tool will legitimately return available:false.
                        characterLoggedIn = ff14bot.Core.Me != null && ff14bot.Core.Me.IsValid
                    });
                    return;

                case "/tools":
                    Respond(context, 200, ToolRegistry.Describe());
                    return;

                case "/rpc":
                    await HandleRpc(context).ConfigureAwait(false);
                    return;

                default:
                    Respond(context, 404, new { ok = false, error = $"Unknown route '{path}'. Try /health, /tools or /rpc." });
                    return;
            }
        }
        catch (Exception ex)
        {
            TryRespond(context, 500, new { ok = false, error = ex.GetBaseException().Message });
        }
    }

    private async Task HandleRpc(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            Respond(context, 405, new { ok = false, error = "/rpc requires POST." });
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

        string toolName;
        JsonElement toolArgs;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            toolName = doc.RootElement.TryGetProperty("tool", out var t) ? t.GetString() : null;

            // Clone: the JsonDocument is disposed at the end of this block, and the handler
            // may run later on the game thread.
            toolArgs = doc.RootElement.TryGetProperty("args", out var a)
                ? a.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Respond(context, 400, new { ok = false, error = $"Malformed JSON: {ex.Message}" });
            return;
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            Respond(context, 400, new { ok = false, error = "Missing 'tool'." });
            return;
        }

        if (!ToolRegistry.TryGet(toolName, out var tool))
        {
            Respond(context, 404, new { ok = false, error = $"Unknown tool '{toolName}'. GET /tools to list." });
            return;
        }

        if (BridgeSettings.Instance.VerboseLogging)
            Logging.Write($"[RbMcp] {toolName} {toolArgs}");

        try
        {
            object result;
            if (tool.NeedsGameThread)
            {
                result = await GameThread
                    .Invoke(() => tool.Handler(toolArgs), BridgeSettings.Instance.ToolTimeoutMs)
                    .ConfigureAwait(false);
            }
            else
            {
                result = tool.Handler(toolArgs);
            }

            Respond(context, 200, new { ok = true, tool = toolName, result });
        }
        catch (TimeoutException ex)
        {
            Respond(context, 504, new { ok = false, tool = toolName, error = ex.Message });
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            Respond(context, 500, new { ok = false, tool = toolName, error = $"{root.GetType().Name}: {root.Message}" });
        }
    }

    private static void Respond(HttpListenerContext context, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private static void TryRespond(HttpListenerContext context, int status, object payload)
    {
        try { Respond(context, status, payload); }
        catch { /* client hung up mid-response; nothing useful left to do */ }
    }
}
