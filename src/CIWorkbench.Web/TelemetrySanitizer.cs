using System.Text.RegularExpressions;

namespace CIWorkbench.Web;

/// <summary>
/// Removes repository identifiers from request paths before they reach telemetry. Repository/owner names can be private
/// and are unbounded in number (high cardinality), so "/repo/acme/secret-project/failures" is exported as "/repo/{owner}/{name}/failures".
/// </summary>
public static partial class TelemetrySanitizer
{
    [GeneratedRegex(@"^/repo/[^/]+/[^/]+(?<rest>/failures)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepoPath();

    public static string SanitizePath(string path)
    {
        var m = RepoPath().Match(path);
        return m.Success ? "/repo/{owner}/{name}" + m.Groups["rest"].Value : path;
    }
}