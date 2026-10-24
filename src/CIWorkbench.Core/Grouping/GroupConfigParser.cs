using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CIWorkbench.Core.Grouping;

/// <summary>
/// Strict reader for <c>.ciworkbench.yml</c>. It never throws: every problem becomes a <see cref="ConfigDiagnostic"/> with a line number,
/// and an invalid entry is skipped (not guessed at) so the rest of the file still applies. Workflows are never hidden by a bad config:
/// anything not placed by a valid entry falls through to the workflow comment and then to "Ungrouped".
/// Errors correspond exactly to violations of <c>schemas/ciworkbench.schema.json</c>; warnings are semantic checks beyond JSON Schema.
/// </summary>
public static partial class GroupConfigParser
{
    private static readonly Regex NameRx = new(GroupConfigContract.GroupNamePattern, RegexOptions.Singleline);
    private static readonly Regex PatternRx = new(GroupConfigContract.WorkflowPattern, RegexOptions.Singleline);

    [GeneratedRegex(@"^[-+]?[0-9]+$")] private static partial Regex IntRx();
    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")] private static partial Regex FloatRx();

    private enum Kind { String, Int, Float, Bool, Null }

    public static GroupConfigParseResult Parse(string? yaml)
    {
        var diags = new List<ConfigDiagnostic>();
        if (string.IsNullOrWhiteSpace(yaml)) return new(new GroupConfig(), diags);

        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            var text = ex.Message; var cut = text.LastIndexOf("): ", StringComparison.Ordinal);
            if (cut >= 0) text = text[(cut + 3)..];
            diags.Add(Error("syntax-error", $"is not valid YAML and was ignored entirely: {text.Split('\n')[0].Trim()}", (ex as YamlException) is { } ye ? (int?)Ln(ye.Start) : null));
            return new(new GroupConfig(), diags);
        }

        if (stream.Documents.Count == 0) return new(new GroupConfig(), diags);                 // comment-only file
        if (stream.Documents.Count > 1)
        {
            diags.Add(Error("invalid-value", "contains more than one YAML document; only one is allowed", Ln(stream.Documents[1].RootNode)));
            return new(new GroupConfig(), diags);
        }

        var rootNode = stream.Documents[0].RootNode;
        if (rootNode is YamlScalarNode { Value: null or "" or "~" or "null" } nullRoot && nullRoot.Style == ScalarStyle.Plain) return new(new GroupConfig(), diags);
        if (rootNode is not YamlMappingNode root)
        {
            diags.Add(Error("invalid-type", $"the top level must be a mapping with optional 'version' and 'groups' (found {Describe(rootNode)})", Ln(rootNode)));
            return new(new GroupConfig(), diags);
        }

        var config = new GroupConfig();
        var versionOk = true;
        YamlNode? groupsNode = null;

        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            switch (key)
            {
                case "version":
                    if (!TryInt(valueNode, out var version)) { diags.Add(Error("invalid-type", $"'version' must be an integer (found {Describe(valueNode)})", Ln(valueNode))); versionOk = false; }
                    else if (version != GroupConfigContract.CurrentVersion)
                    {
                        diags.Add(Error("unsupported-version", $"'version: {version}' is not supported by this CIWorkbench (supported: {GroupConfigContract.CurrentVersion}); the file was ignored", Ln(valueNode)));
                        versionOk = false;
                    }
                    break;
                case "groups": groupsNode = valueNode; break;
                default: diags.Add(UnknownField(key ?? "?", GroupConfigContract.RootFields, Ln(keyNode), "at the top level")); break;
            }
        }

        var entries = new List<GroupConfigEntry>();
        if (groupsNode is not null)
        {
            if (groupsNode is not YamlSequenceNode seq)
                diags.Add(Error("invalid-type", $"'groups' must be a list of groups (found {Describe(groupsNode)})", Ln(groupsNode)));
            else
                for (var i = 0; i < seq.Children.Count; i++)
                    if (ParseEntry(seq.Children[i], i + 1, diags) is { } entry) entries.Add(entry);
        }

        if (!versionOk) return new(new GroupConfig(), diags);          // unknown semantics: apply nothing, report clearly
        config.Groups = entries;
        AddSemanticWarnings(entries, diags);
        return new(config, diags);
    }

    private static GroupConfigEntry? ParseEntry(YamlNode node, int index, List<ConfigDiagnostic> diags)
    {
        var before = diags.Count(d => d.Severity == ConfigSeverity.Error);
        var label = $"groups[{index}]";
        if (node is not YamlMappingNode map)
        {
            diags.Add(Error("invalid-type", $"{label} must be a mapping with 'name' and optional 'description' and 'workflows' (found {Describe(node)})", Ln(node)));
            diags.Add(Skipped(label, Ln(node)));
            return null;
        }

        var entry = new GroupConfigEntry { Line = Ln(node) };
        var hasName = false;
        foreach (var (keyNode, valueNode) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value;
            switch (key)
            {
                case "name":
                    hasName = true;
                    if (StringValue(valueNode, $"{label}.name", diags) is { } name)
                    {
                        if (name.Length == 0 || name.Length > GroupConfigContract.MaxNameLength || !NameRx.IsMatch(name))
                            diags.Add(Error("invalid-value", $"{label}.name '{Trunc(name)}' is not valid: it must be 1-{GroupConfigContract.MaxNameLength} characters, must not start or end with '/' or whitespace, and must not contain '//'", Ln(valueNode)));
                        else entry.Name = name;
                    }
                    break;
                case "description":
                    if (StringValue(valueNode, $"{label}.description", diags) is { } desc)
                    {
                        if (desc.Length > GroupConfigContract.MaxDescriptionLength)
                            diags.Add(Error("invalid-value", $"{label}.description is longer than {GroupConfigContract.MaxDescriptionLength} characters", Ln(valueNode)));
                        else entry.Description = desc;
                    }
                    break;
                case "workflows":
                    if (valueNode is not YamlSequenceNode list)
                    { diags.Add(Error("invalid-type", $"{label}.workflows must be a list of workflow paths (found {Describe(valueNode)})", Ln(valueNode))); break; }
                    foreach (var item in list.Children)
                    {
                        if (StringValue(item, $"{label}.workflows[]", diags) is not { } pattern) continue;
                        if (pattern.Length == 0 || pattern.Length > GroupConfigContract.MaxWorkflowPatternLength || !PatternRx.IsMatch(pattern))
                            diags.Add(Error("invalid-value", $"workflow pattern '{Trunc(pattern)}' in {label} is not valid: {PatternProblem(pattern)}", Ln(item)));
                        else { entry.Workflows.Add(pattern); entry.WorkflowLines.Add(Ln(item)); }
                    }
                    break;
                default: diags.Add(UnknownField(key ?? "?", GroupConfigContract.GroupFields, Ln(keyNode), $"in {label}")); break;
            }
        }

        if (!hasName) diags.Add(Error("missing-field", $"{label} is missing the required field 'name'", Ln(node)));

        if (diags.Count(d => d.Severity == ConfigSeverity.Error) > before)
        {
            diags.Add(Skipped(entry.Name.Length > 0 ? $"{label} ('{entry.Name}')" : label, Ln(node)));
            return null;
        }
        return entry;
    }

    private static int Ln(YamlNode n) => (int)n.Start.Line;
    private static int Ln(Mark m) => (int)m.Line;

    private static string? StringValue(YamlNode node, string path, List<ConfigDiagnostic> diags)
    {
        if (node is YamlScalarNode s && KindOf(s) == Kind.String) return s.Value ?? "";
        diags.Add(Error("invalid-type", $"{path} must be text (found {Describe(node)}); put quotes around it if it only looks like a number or boolean", Ln(node)));
        return null;
    }

    private static void AddSemanticWarnings(List<GroupConfigEntry> entries, List<ConfigDiagnostic> diags)
    {
        var names = new Dictionary<string, GroupConfigEntry>(StringComparer.OrdinalIgnoreCase);
        var patterns = new Dictionary<string, (GroupConfigEntry Owner, int Line)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var norm = string.Join('/', e.Name.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (names.TryGetValue(norm, out var first))
                diags.Add(new(ConfigSeverity.Warning, "duplicate-group", $"group '{norm}' is defined more than once (first at line {first.Line}); the entries are merged and the first description wins", e.Line));
            else names[norm] = e;

            for (var i = 0; i < e.Workflows.Count; i++)
            {
                var p = e.Workflows[i].Replace('\\', '/');
                var line = e.LineOfPattern(i) ?? 0;
                if (patterns.TryGetValue(p, out var prior))
                    diags.Add(new(ConfigSeverity.Warning, "duplicate-pattern",
                        ReferenceEquals(prior.Owner, e)
                            ? $"'{p}' is listed twice in group '{e.Name}' (first at line {prior.Line})"
                            : $"'{p}' is assigned to both '{prior.Owner.Name}' (line {prior.Line}) and '{e.Name}'; the first group wins", line));
                else patterns[p] = (e, line);
            }
        }
    }

    private static string PatternProblem(string p) =>
        p.Length == 0 ? "it is empty"
        : p.Length > GroupConfigContract.MaxWorkflowPatternLength ? $"it is longer than {GroupConfigContract.MaxWorkflowPatternLength} characters"
        : p.Any(char.IsWhiteSpace) ? "whitespace is not allowed"
        : p.Contains("**") ? "'**' is not supported; use a single '*' (it matches inside one path segment)"
        : "'?', '[', ']', '{' and '}' are not supported; the only wildcard is '*'";

    private static bool TryInt(YamlNode node, out long value)
    {
        value = 0;
        return node is YamlScalarNode s && KindOf(s) == Kind.Int && long.TryParse(s.Value, out value);
    }

    // YAML 1.2 core-schema typing of plain scalars; quoted scalars are always text.
    private static Kind KindOf(YamlScalarNode s)
    {
        if (s.Style != ScalarStyle.Plain) return Kind.String;
        var v = s.Value ?? "";
        if (v is "" or "~" or "null" or "Null" or "NULL") return Kind.Null;
        if (v is "true" or "True" or "TRUE" or "false" or "False" or "FALSE") return Kind.Bool;
        if (IntRx().IsMatch(v)) return Kind.Int;
        if (FloatRx().IsMatch(v) || v is ".inf" or "-.inf" or "+.inf" or ".nan" or ".NaN" or ".NAN") return Kind.Float;
        return Kind.String;
    }

    private static string Describe(YamlNode n) => n switch
    {
        YamlMappingNode => "a mapping",
        YamlSequenceNode => "a list",
        YamlScalarNode s => KindOf(s) switch { Kind.Int or Kind.Float => "a number", Kind.Bool => "a boolean", Kind.Null => "an empty value", _ => "text" },
        _ => "an unsupported value",
    };

    private static ConfigDiagnostic UnknownField(string key, IReadOnlyList<string> known, int line, string where)
    {
        var best = known.Select(k => (k, d: Distance(key.ToLowerInvariant(), k))).OrderBy(x => x.d).First();
        var hint = best.d <= 2 ? $" (did you mean '{best.k}'?)" : $" (allowed: {string.Join(", ", known.Select(k => $"'{k}'"))})";
        return Error("unknown-field", $"unknown field '{key}' {where}{hint}", line);
    }

    private static ConfigDiagnostic Skipped(string label, int line) =>
        new(ConfigSeverity.Warning, "group-skipped", $"{label} was skipped because of the error(s) above; its workflows fall back to workflow comments or 'Ungrouped'", line);

    private static ConfigDiagnostic Error(string code, string message, int? line) => new(ConfigSeverity.Error, code, message, line);
    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
