using System.Net;

namespace CIWorkbench.Web;

/// <summary>Why live mode can never work on this host, regardless of the token (set once at startup).</summary>
public sealed record LiveModePolicy(bool BlockedByNetworkExposure);

/// <summary>
/// The standalone host is single-user and authenticates nothing, so a configured GitHub token must never be reachable from another machine.
/// When any configured listen address is not loopback, the token is dropped at startup and only demo data is served.
/// </summary>
public static class NetworkExposure
{
    /// <summary>True when every address is loopback (localhost, 127.x, ::1). No addresses configured means Kestrel's default, which is loopback.</summary>
    public static bool IsLoopbackOnly(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return true;
        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var u = raw.Replace("*", "0.0.0.0", StringComparison.Ordinal).Replace("+", "0.0.0.0", StringComparison.Ordinal);
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return false;
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether anything configured would let another machine connect. Kestrel precedence is mirrored: explicit URLs win over the
    /// HTTP_PORTS/HTTPS_PORTS shortcuts (which bind every interface), and explicit Kestrel endpoint URLs are always checked.
    /// </summary>
    public static bool IsRemoteReachable(string? urls, string? portsShortcut, IEnumerable<string?> kestrelEndpointUrls)
    {
        if (!IsLoopbackOnly(urls)) return true;
        if (string.IsNullOrWhiteSpace(urls) && !string.IsNullOrWhiteSpace(portsShortcut)) return true;
        return kestrelEndpointUrls.Any(u => !string.IsNullOrWhiteSpace(u) && !IsLoopbackOnly(u));
    }
}
