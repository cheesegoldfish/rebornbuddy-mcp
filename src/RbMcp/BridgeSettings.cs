using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ff14bot.Helpers;

namespace RbMcp;

/// <summary>
/// Plain settings POCO, persisted next to the plugin DLL.
///
/// Deliberately does NOT use RB's <c>JsonSettings</c> base. That would pull in
/// <c>[Setting]</c> from System.Configuration.ConfigurationManager, which is neither in
/// the .NET 8 shared framework nor present as a file in RebornBuddy's folder - RB carries
/// it embedded. A plugin referencing it therefore fails to load, and RB swallows the
/// failure: the plugin simply never appears in the list, with nothing in the log.
///
/// Keeping the dependency surface to RebornBuddy.dll plus the shared framework is what
/// makes this loadable at all.
/// </summary>
internal sealed class BridgeSettings
{
    private static readonly Lazy<BridgeSettings> Lazy = new(Load);

    [JsonIgnore]
    public static BridgeSettings Instance => Lazy.Value;

    public int Port { get; set; } = 8787;

    public bool AutoStart { get; set; } = true;

    public int EvalTimeoutMs { get; set; } = 5000;

    public int ToolTimeoutMs { get; set; } = 5000;

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Whether <see cref="Tracking.WorldTracker"/> is cataloguing statuses and casts on
    /// everything nearby.
    ///
    /// Off by default, and worth knowing why before turning it on: the ledger records the
    /// character names of other players near you, durably, to disk. That is useful for
    /// "what was that status" archaeology and it is other people's data, so it is opt-in
    /// and the plugin button toggles it.
    /// </summary>
    public bool RecordObservations { get; set; }

    /// <summary>
    /// Units whose auras are read per sample tick (four ticks a second).
    ///
    /// This is the cost ceiling for observation tracking. Aura enumeration is roughly thirty
    /// lazy memory reads per unit, so scanning a full seventy-player zone every tick would be
    /// thousands of cross-process reads a second on the pulse thread. Budgeting means cost is
    /// flat regardless of how crowded the zone is; the trade is latency, since a crowd of N
    /// takes N/budget ticks to sweep. At 8 a seventy-player zone sweeps in about two seconds,
    /// which is well inside the lifetime of anything worth cataloguing.
    /// </summary>
    public int ObservationScanBudget { get; set; } = 8;

    /// <summary>
    /// Require a bearer token on /tools and /rpc.
    ///
    /// On by default, and you should leave it on. It is what stops a web page you happen to
    /// be viewing from driving code inside RebornBuddy - see <see cref="Http.Guard"/> for
    /// why loopback alone does not cover that. Turning it off is supported because a
    /// throwaway VM or a fully offline box is a legitimate setup, not because it is safe.
    ///
    /// Note there is deliberately no capability restriction anywhere in this plugin. A
    /// snippet that gets in may cast, move, and reflect over loaded routines. The whole
    /// value of the tool is that an agent can drive the game to answer a question, so the
    /// boundary is who may call, never what they may call.
    /// </summary>
    public bool RequireAuthToken { get; set; } = true;

    /// <summary>
    /// Loopback only, and not configurable. This endpoint executes arbitrary code; binding
    /// it to anything routable would be a remote shell.
    /// </summary>
    [JsonIgnore]
    public string Host => "127.0.0.1";

    [JsonIgnore]
    public static string FilePath => PluginPaths.Combine("RbMcp.json");

    private static BridgeSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not read settings ({ex.Message}); using defaults.");
        }

        // Write defaults out on first run so the file is discoverable and editable.
        var settings = new BridgeSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not save settings: {ex.Message}");
        }
    }
}
