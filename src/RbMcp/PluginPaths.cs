using System;
using System.IO;
using System.Reflection;
using ff14bot.Helpers;
using ff14bot.Settings;

namespace RbMcp;

/// <summary>
/// Resolves the folder this plugin's files belong in.
///
/// **`Assembly.GetExecutingAssembly().Location` is an empty string here.** The loader loads
/// the DLL from a byte array precisely so RB does not lock the file and hot reload works,
/// and a byte-loaded assembly has no location on disk. Anything deriving a path from it
/// silently collapses to <c>Path.Combine(".", ...)</c> — RebornBuddy's working directory,
/// not the plugin folder.
///
/// That was harmless for as long as nothing outside the process needed to find these files:
/// settings and the observation ledger simply lived one directory up from where you would
/// look for them. It stopped being harmless when the MCP shim started reading the auth token
/// off disk, at a path `deploy.ps1` computes as
/// <c>&lt;RebornBuddy&gt;\Plugins\RbMcp\RbMcp.token</c>. Written to the wrong folder, the
/// token file the shim needs never appears and every /rpc call 401s.
///
/// So ask RB where its plugins live rather than inferring it. <c>GlobalSettings.PluginsPath</c>
/// is what the loader itself uses to find the DLL, which makes it the same answer by
/// construction.
/// </summary>
internal static class PluginPaths
{
    /// <summary>Must match the folder name deploy.ps1 and the csproj copy into.</summary>
    private const string PluginFolderName = "RbMcp";

    private static string _cached;

    public static string Folder => _cached ??= Resolve();

    public static string Combine(string fileName) => Path.Combine(Folder, fileName);

    private static string Resolve()
    {
        // If this assembly was ever loaded normally (not via the byte-loading loader),
        // its own location is the most direct answer.
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(location))
            {
                var dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
        }
        catch
        {
            // Reflection over a dynamic assembly can throw rather than return empty.
        }

        // The normal case. Same lookup the loader uses.
        try
        {
            var pluginsPath = GlobalSettings.Instance?.PluginsPath;
            if (!string.IsNullOrEmpty(pluginsPath))
            {
                var dir = Path.Combine(pluginsPath, PluginFolderName);
                System.IO.Directory.CreateDirectory(dir);
                return Path.GetFullPath(dir);
            }
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not resolve the plugin folder from RB settings: {ex.Message}");
        }

        // Last resort: the old behaviour. Log it, because the token will be somewhere the
        // shim is not looking and the failure would otherwise present as a bare 401.
        var fallback = Path.GetFullPath(".");
        Logging.Write($"[RbMcp] Falling back to the working directory for plugin files: {fallback}");
        return fallback;
    }
}
