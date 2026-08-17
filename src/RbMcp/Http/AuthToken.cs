using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ff14bot.Helpers;

namespace RbMcp.Http;

/// <summary>
/// Shared secret between the bridge and whatever is driving it.
///
/// Loopback is not a trust boundary. This process executes arbitrary code, it listens on a
/// fixed well-known port, and RebornBuddy is usually elevated so it can read game memory.
/// Without a secret, every web page the browser loads can POST to 127.0.0.1:8787 and drive
/// code inside an administrator process - see <see cref="Guard"/> for the mechanics.
///
/// A random token in a file closes that vector outright: a page in a browser sandbox cannot
/// read a file off disk, and a local script has to be told where to look.
///
/// Honest limit, so nobody oversells this in a README: the file carries whatever ACL it
/// inherits from the plugin folder, so any process already running as you can read it. The
/// obvious tightening - System.IO.FileSystem.AccessControl - is not available reliably:
/// it ships in some .NET 8 patch installs and not others (8.0.6 carries it, 8.0.5 does
/// not), and a plugin referencing a missing assembly fails to load with nothing in RB's
/// log. Not a trade worth making for a boundary that only holds against malware already
/// running under your account.
/// </summary>
internal static class AuthToken
{
    private const string FileName = "RbMcp.token";

    private static readonly object Lock = new();
    private static string _cached;

    /// <summary>
    /// Beside the plugin DLL, next to RbMcp.json. Deliberately not in the repo - the token
    /// belongs to an install, not to the source tree.
    ///
    /// Must agree with the path deploy.ps1 writes into the MCP registration, which is why
    /// this goes through <see cref="PluginPaths"/> rather than the assembly location - see
    /// that class for why the obvious approach silently resolves elsewhere.
    /// </summary>
    public static string FilePath => PluginPaths.Combine(FileName);

    /// <summary>
    /// The current token, generated and persisted on first use. Returns null only if the
    /// file could not be written, in which case the caller decides whether to fail open.
    /// </summary>
    public static string Value
    {
        get
        {
            if (_cached != null) return _cached;

            lock (Lock)
            {
                if (_cached != null) return _cached;
                _cached = LoadOrCreate();
                return _cached;
            }
        }
    }

    private static string LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var existing = File.ReadAllText(FilePath).Trim();
                if (existing.Length >= 32) return existing;

                // Present but unusable (truncated, hand-edited, empty). Replacing it is the
                // right call - a short token is worse than an obviously rotated one.
                Logging.Write("[RbMcp] Auth token file was malformed; generating a new one.");
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');

            File.WriteAllText(FilePath, token);
            Logging.Write($"[RbMcp] Generated a new auth token at {FilePath}");
            return token;
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not establish an auth token: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Constant-time comparison. Overkill against a local attacker who can already time
    /// nothing useful over loopback, but string equality on a secret is the kind of thing
    /// that gets copied into somewhere it does matter.
    /// </summary>
    public static bool Matches(string presented)
    {
        var expected = Value;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);

        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
