using System;
using System.Net;

namespace RbMcp.Http;

/// <summary>
/// Decides whether a request is allowed to reach a handler.
///
/// The thing worth understanding before changing any of this: <b>binding to 127.0.0.1 keeps
/// the bridge off the network, but it does not keep it away from attackers.</b> Every page
/// the browser loads runs code on this machine, and that code can reach loopback.
///
/// The concrete attack, in the order a page would try it:
///
///  1. <b>Simple-request CSRF.</b> A page on any origin can fire
///     <c>fetch('http://127.0.0.1:8787/rpc', {mode:'no-cors', body: ...})</c>. With a
///     Content-Type of text/plain, form-urlencoded or multipart there is <i>no preflight</i> -
///     the request lands and executes. Same-origin policy stops the page reading the
///     response, which is no comfort at all when the request itself is the payload.
///     Countered by demanding application/json on /rpc: that content type is not "simple",
///     so the browser must preflight, and we answer no preflight successfully.
///
///  2. <b>DNS rebinding.</b> Defeats the assumption behind (1) that the page is cross-origin.
///     The attacker points evil.com at 127.0.0.1 after a short TTL, at which point the page
///     is same-origin with us and may preflight and read freely. What it cannot forge is the
///     Host header, which still says evil.com. Countered by requiring a loopback host.
///     HTTP.SYS may reject some of these before we see them, given the prefix is an explicit
///     IP - but that is an implementation detail of the host, not a guarantee we own.
///
///  3. <b>Everything else on the machine.</b> Any local process can speak HTTP to a known
///     port. Countered by <see cref="AuthToken"/>, which is a file a browser cannot read.
///
/// Note what is deliberately absent: no CORS headers are ever emitted, and OPTIONS is not
/// answered. A preflight that never succeeds is the point.
///
/// None of this restricts what a snippet may do once admitted. Capability is not the
/// boundary here - the caller's identity is. An agent driving this on your behalf should be
/// able to cast, move, and reflect over loaded routines, because that is the entire job.
/// </summary>
internal static class Guard
{
    /// <summary>Routes that answer without a token, so setup can be debugged with curl.</summary>
    public static bool IsPublicRoute(string path) => path is "" or "/health";

    public static GuardResult Check(HttpListenerRequest request, string path)
    {
        // (a) Anything carrying browser provenance is refused outright. The MCP shim, curl,
        //     and any script send none of these; a page cannot suppress them.
        if (!string.IsNullOrEmpty(request.Headers["Origin"]))
            return GuardResult.Deny(403, "Requests carrying an Origin header are refused.");

        if (!string.IsNullOrEmpty(request.Headers["Referer"]))
            return GuardResult.Deny(403, "Requests carrying a Referer header are refused.");

        // Fetch Metadata: browsers label their own requests. 'none' means a direct user
        // navigation, which is harmless; anything else means a page drove it.
        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(fetchSite) &&
            !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Deny(403, $"Browser-originated request refused (Sec-Fetch-Site: {fetchSite}).");
        }

        // (b) Host must name loopback, on our port. Kills DNS rebinding, because Host
        //     reflects the hostname in the URL the page requested and a rebinding attack
        //     is still spelling that evil.com.
        //
        //     'localhost' is accepted alongside the IP literals even though it is a name,
        //     not a literal. It costs nothing: to get 'Host: localhost' past this check a
        //     page must have been served from localhost:8787, and the only thing on that
        //     port is us - we serve no HTML for a page to originate from. Rejecting it
        //     would only break the curl invocation everyone reaches for first.
        var host = request.Headers["Host"] ?? request.UserHostName;
        if (!IsLoopbackHost(host, BridgeSettings.Instance.Port))
            return GuardResult.Deny(403, $"Unexpected Host header '{host}'. Reach the bridge as 127.0.0.1.");

        // (c) /rpc must be JSON, which forces a preflight the attacker cannot satisfy.
        if (path == "/rpc")
        {
            var contentType = request.ContentType ?? string.Empty;
            var semi = contentType.IndexOf(';');
            if (semi >= 0) contentType = contentType.Substring(0, semi);

            if (!contentType.Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Deny(415,
                    "/rpc requires Content-Type: application/json. This is load-bearing: the " +
                    "simple content types are exactly the ones a hostile page can send without " +
                    "a CORS preflight.");
            }
        }

        // (d) The shared secret, last so a legitimate caller missing only the token gets a
        //     401 that says so rather than a confusing 403.
        if (BridgeSettings.Instance.RequireAuthToken && !IsPublicRoute(path))
        {
            var presented = ExtractToken(request);
            if (string.IsNullOrEmpty(presented))
            {
                return GuardResult.Deny(401,
                    $"Missing auth token. Send 'Authorization: Bearer <token>' using the value in {AuthToken.FilePath}.");
            }

            if (!AuthToken.Matches(presented))
                return GuardResult.Deny(401, "Auth token does not match.");
        }

        return GuardResult.Allow();
    }

    private static string ExtractToken(HttpListenerRequest request)
    {
        var auth = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth.Substring(7).Trim();

        // Convenience for curl and scratch scripts, where a bearer header is fiddly.
        return request.Headers["X-RbMcp-Token"]?.Trim();
    }

    private static bool IsLoopbackHost(string host, int port)
    {
        if (string.IsNullOrEmpty(host)) return false;

        // Strip the port, minding IPv6's bracketed form.
        string name;
        if (host.StartsWith("["))
        {
            var close = host.IndexOf(']');
            if (close < 0) return false;
            name = host.Substring(0, close + 1);
            var rest = host.Substring(close + 1);
            if (rest.Length > 0 && (!rest.StartsWith(":") || rest.Substring(1) != port.ToString()))
                return false;
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0)
            {
                if (host.Substring(colon + 1) != port.ToString()) return false;
                name = host.Substring(0, colon);
            }
            else
            {
                name = host;
            }
        }

        return name is "127.0.0.1" or "localhost" or "[::1]";
    }
}

internal readonly struct GuardResult
{
    private GuardResult(bool allowed, int status, string reason)
    {
        Allowed = allowed;
        Status = status;
        Reason = reason;
    }

    public bool Allowed { get; }
    public int Status { get; }
    public string Reason { get; }

    public static GuardResult Allow() => new(true, 200, null);
    public static GuardResult Deny(int status, string reason) => new(false, status, reason);
}
