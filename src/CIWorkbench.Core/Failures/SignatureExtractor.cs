using System.Text.RegularExpressions;

namespace CIWorkbench.Core.Failures;

public enum SignatureKind { Known, Unknown, LogUnavailable }

/// <param name="Normalized">Stable text used for grouping (null unless Known).</param>
/// <param name="Raw">Original candidate line shown to the user.</param>
public sealed record FailureSignature(SignatureKind Kind, string? Normalized, string? Raw)
{
    public static readonly FailureSignature Unknown = new(SignatureKind.Unknown, null, null);
    public static readonly FailureSignature Unavailable = new(SignatureKind.LogUnavailable, null, null);
}

/// <summary>
/// Deterministic log -> signature extraction. A bare "exit code 1" is never a signature.
/// Scans only the tail of a bounded log.
/// </summary>
public static partial class SignatureExtractor
{
    public const int MaxLinesScanned = 400;
    public const int MaxSignatureLength = 200;

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")] private static partial Regex Ansi();
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z\s?")] private static partial Regex Timestamp();
    [GeneratedRegex(@"^##\[(error|warning|group|endgroup)\]", RegexOptions.IgnoreCase)] private static partial Regex Marker();
    [GeneratedRegex(@"(?:/home/runner/work/[^/\s]+/[^/\s]+|/Users/runner/work/[^/\s]+/[^/\s]+|[A-Za-z]:\\a\\[^\\\s]+\\[^\\\s]+|/tmp/[^\s/]+|/home/runner/[^\s]*?(?=/))[/\\]?")] private static partial Regex WorkspacePath();
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")] private static partial Regex Guid();
    [GeneratedRegex(@"\b\d{4}-\d\d-\d\d[T ][\d:.]+Z?\b")] private static partial Regex IsoDate();
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?(?:ms|s|sec|seconds|m|min)\b")] private static partial Regex Duration();
    [GeneratedRegex(@"\b\d\d:\d\d:\d\d(?:\.\d+)?\b")] private static partial Regex Clock();
    [GeneratedRegex(@"\b(?=[0-9a-fA-F]*\d)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{7,64}\b")] private static partial Regex Hex();
    [GeneratedRegex(@"(?<=:)\d{2,5}(?=/|\b)")] private static partial Regex Port();
    [GeneratedRegex(@"[\(\[:]\d+[,:]\d+[\)\]]?")] private static partial Regex LineCol();
    [GeneratedRegex(@"\b\d{4,}\b")] private static partial Regex BigNumber();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    [GeneratedRegex(@"exit code \d+|exited with code \d+|The operation was canceled|##\[error\]Process completed", RegexOptions.IgnoreCase)] private static partial Regex GenericExit();
    [GeneratedRegex(@"\berror\s+[A-Z]{2,}\d+\s*:|\bnpm ERR!|\bAssertionError\b|^\s*(?:Failed|FAILED)\b|\b(?:[A-Za-z]*Exception|Error):|^\s*fatal:|\bTimeoutError\b|\bpanic:|\bE\s+[A-Za-z]+Error", RegexOptions.IgnoreCase)] private static partial Regex ToolShape();

    public static string StripDecorations(string line)
    {
        line = Ansi().Replace(line, "");
        line = Timestamp().Replace(line.TrimStart(), "");
        line = Marker().Replace(line, "").Trim();
        return line;
    }

    public static string Normalize(string line)
    {
        var s = StripDecorations(line);
        s = WorkspacePath().Replace(s, "<ws>/");
        s = Guid().Replace(s, "<guid>");
        s = IsoDate().Replace(s, "<ts>");
        s = Clock().Replace(s, "<time>");
        s = Duration().Replace(s, "<dur>");
        s = Hex().Replace(s, "<hex>");
        s = Port().Replace(s, "<port>");
        s = LineCol().Replace(s, "(L,C)");
        s = BigNumber().Replace(s, "<n>");
        s = Spaces().Replace(s, " ").Trim();
        return s.Length > MaxSignatureLength ? s[..MaxSignatureLength] : s;
    }

    public static FailureSignature Extract(string? logTail)
    {
        if (logTail is null) return FailureSignature.Unavailable;
        var lines = logTail.Split('\n');
        var start = Math.Max(0, lines.Length - MaxLinesScanned);
        var scan = lines.Skip(start).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        string? pick = null;
        // 1. explicit ##[error] lines that are not generic
        foreach (var l in scan)
        {
            var clean = Ansi().Replace(l, "");
            if (clean.Contains("##[error]", StringComparison.Ordinal) && !GenericExit().IsMatch(clean) && StripDecorations(l).Length > 0)
            { pick = l; break; }
        }
        // 2. known tool shapes (first match in the tail)
        pick ??= scan.FirstOrDefault(l => { var c = StripDecorations(l); return c.Length > 0 && !GenericExit().IsMatch(c) && ToolShape().IsMatch(c); });
        // 3. last non-generic line before the generic exit line
        if (pick is null)
        {
            var exitIdx = scan.FindLastIndex(l => GenericExit().IsMatch(Ansi().Replace(l, "")));
            if (exitIdx > 0)
            {
                for (var i = exitIdx - 1; i >= 0; i--)
                {
                    var c = StripDecorations(scan[i]);
                    if (c.Length > 3 && !GenericExit().IsMatch(c) && !c.StartsWith("Run ", StringComparison.Ordinal)) { pick = scan[i]; break; }
                }
            }
        }
        if (pick is null) return FailureSignature.Unknown;
        var norm = Normalize(pick);
        return norm.Length == 0 ? FailureSignature.Unknown : new FailureSignature(SignatureKind.Known, norm, StripDecorations(pick));
    }
}

