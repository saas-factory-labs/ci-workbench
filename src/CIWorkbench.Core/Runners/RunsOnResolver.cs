using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CIWorkbench.Core.Runners;

/// <summary>
/// How sure a configured runner is. A fallback literal (<c>vars.X || '[...]'</c>) is only what runs when the variable is unset:
/// it is a potential runner, never reported as the active one. Actual runners always come from executed jobs, not from here.
/// </summary>
public enum RunnerRole { Configured, Fallback, Unknown }

/// <param name="Source">Where the labels come from: a literal, a variable, a matrix key or an expression.</param>
public sealed record ConfiguredRunner(string JobId, string JobName, IReadOnlyList<string> Labels, RunnerRole Role, string Source, string? Note = null);

/// <param name="LocalReusable">Job id → repository path of a <c>uses: ./...</c> reusable workflow whose own jobs decide the runner.</param>
public sealed record WorkflowRunnerConfig(string WorkflowPath, IReadOnlyList<ConfiguredRunner> Runners, IReadOnlyDictionary<string, string> LocalReusable, bool ParseFailed)
{
    public static WorkflowRunnerConfig Failed(string path) => new(path, [], new Dictionary<string, string>(), true);
}

/// <summary>
/// Reads <c>runs-on</c> from workflow YAML. Handles labels, label arrays, <c>{group, labels}</c>, matrix keys with literal values,
/// <c>fromJSON(vars.X || '[...]')</c>, other expressions (reported as unknown, with quoted literals as potential fallbacks)
/// and reusable workflows (local ones are returned for the caller to follow; remote ones are unknown). Never throws.
/// </summary>
public static partial class RunsOnResolver
{
    [GeneratedRegex(@"^(?:from(?:JSON|Json)\(\s*)?vars\.(?<n>[A-Za-z_]\w*)\s*(?:\|\|\s*'(?<lit>(?:[^']|'')*)')?\s*\)?$")] private static partial Regex VarExpr();
    [GeneratedRegex(@"^matrix\.(?<k>[A-Za-z_][\w-]*)$")] private static partial Regex MatrixExpr();
    [GeneratedRegex(@"'(?<s>(?:[^']|'')*)'")] private static partial Regex Quoted();

    /// <param name="vars">Actions variables as read from GitHub; null when they could not be read (then <c>vars.X</c> is unknown, not unset).</param>
    public static WorkflowRunnerConfig Resolve(string workflowPath, string? yaml, IReadOnlyDictionary<string, string>? vars)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return WorkflowRunnerConfig.Failed(workflowPath);
        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (YamlException) { return WorkflowRunnerConfig.Failed(workflowPath); }
        if (root is null || Child(root, "jobs") is not YamlMappingNode jobs) return WorkflowRunnerConfig.Failed(workflowPath);

        var runners = new List<ConfiguredRunner>();
        var reusable = new Dictionary<string, string>();
        foreach (var (keyNode, valueNode) in jobs.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } jobId } || valueNode is not YamlMappingNode job) continue;
            var name = (Child(job, "name") as YamlScalarNode)?.Value ?? jobId;

            if (Child(job, "uses") is YamlScalarNode { Value: { } uses })
            {
                if (uses.StartsWith("./", StringComparison.Ordinal)) reusable[jobId] = uses[2..].Split('@')[0];
                else runners.Add(new ConfiguredRunner(jobId, name, [], RunnerRole.Unknown, "reusable workflow", $"Runner is chosen inside the remote workflow {uses}."));
                continue;
            }
            if (Child(job, "runs-on") is not { } runsOn) continue;
            foreach (var r in ResolveRunsOn(jobId, name, runsOn, job, vars)) runners.Add(r);
        }
        return new WorkflowRunnerConfig(workflowPath, runners, reusable, false);
    }

    private static IEnumerable<ConfiguredRunner> ResolveRunsOn(string jobId, string jobName, YamlNode node, YamlMappingNode job, IReadOnlyDictionary<string, string>? vars)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { } s }:
                return Scalar(jobId, jobName, s, job, vars);
            case YamlSequenceNode seq:
            {
                var items = seq.Children.OfType<YamlScalarNode>().Select(c => c.Value ?? "").ToList();
                var expr = items.FirstOrDefault(i => i.Contains("${{", StringComparison.Ordinal));
                return expr is not null ? Scalar(jobId, jobName, expr, job, vars) : [new ConfiguredRunner(jobId, jobName, items, RunnerRole.Configured, "literal")];
            }
            case YamlMappingNode map:
            {
                var group = (Child(map, "group") as YamlScalarNode)?.Value;
                var note = group is null ? null : $"Runner group: {group}";
                var inner = Child(map, "labels") is { } l ? ResolveRunsOn(jobId, jobName, l, job, vars).ToList() : [];
                if (inner.Count == 0) return [new ConfiguredRunner(jobId, jobName, [], RunnerRole.Unknown, "runner group", note ?? "No labels given.")];
                return inner.Select(r => r with { Note = r.Note ?? note });
            }
        }
        return [];
    }

    private static IEnumerable<ConfiguredRunner> Scalar(string jobId, string jobName, string text, YamlMappingNode job, IReadOnlyDictionary<string, string>? vars)
    {
        var t = text.Trim();
        if (!t.Contains("${{", StringComparison.Ordinal)) return [new ConfiguredRunner(jobId, jobName, [t], RunnerRole.Configured, "literal")];

        var open = t.IndexOf("${{", StringComparison.Ordinal); var close = t.LastIndexOf("}}", StringComparison.Ordinal);
        var expr = close > open ? t[(open + 3)..close].Trim() : t;

        if (VarExpr().Match(expr) is { Success: true } v) return Variable(jobId, jobName, v.Groups["n"].Value, v.Groups["lit"].Success ? v.Groups["lit"].Value.Replace("''", "'") : null, vars);
        if (MatrixExpr().Match(expr) is { Success: true } m) return Matrix(jobId, jobName, m.Groups["k"].Value, job);

        // Anything else is dynamic. Quoted literals inside it are only candidates, never claimed as the runner in use.
        var candidates = Quoted().Matches(expr).Select(x => x.Groups["s"].Value).Where(s => s.Length > 0).Distinct().ToList();
        var result = new List<ConfiguredRunner> { new(jobId, jobName, [], RunnerRole.Unknown, "expression", $"Resolved at run time: {expr}") };
        result.AddRange(candidates.Select(c => new ConfiguredRunner(jobId, jobName, ParseLabels(c), RunnerRole.Fallback, "expression", "Possible value seen in the expression; not necessarily used.")));
        return result;
    }

    private static IEnumerable<ConfiguredRunner> Variable(string jobId, string jobName, string name, string? literal, IReadOnlyDictionary<string, string>? vars)
    {
        var fallback = literal is null ? null : ParseLabels(literal);
        if (vars is null)
        {
            var list = new List<ConfiguredRunner> { new(jobId, jobName, [], RunnerRole.Unknown, $"vars.{name}", "The variable could not be read (needs the Variables: Read permission), so the runner in use is unknown.") };
            if (fallback is not null) list.Add(new(jobId, jobName, fallback, RunnerRole.Fallback, $"fallback of vars.{name}", "Used only when the variable is unset."));
            return list;
        }
        if (vars.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            var list = new List<ConfiguredRunner> { new(jobId, jobName, ParseLabels(value), RunnerRole.Configured, $"vars.{name}") };
            if (fallback is not null && !fallback.SequenceEqual(list[0].Labels, StringComparer.OrdinalIgnoreCase)) list.Add(new(jobId, jobName, fallback, RunnerRole.Fallback, $"fallback of vars.{name}", "Used only when the variable is unset."));
            return list;
        }
        // The variable is genuinely unset or empty, so the expression evaluates to its literal.
        return fallback is not null
            ? [new ConfiguredRunner(jobId, jobName, fallback, RunnerRole.Configured, $"default of vars.{name}", "The variable is not set, so this default applies.")]
            : [new ConfiguredRunner(jobId, jobName, [], RunnerRole.Unknown, $"vars.{name}", "The variable is not set and has no default.")];
    }

    private static IEnumerable<ConfiguredRunner> Matrix(string jobId, string jobName, string key, YamlMappingNode job)
    {
        if (Child(Child(job, "strategy"), "matrix") is not YamlMappingNode matrix)
            return [new ConfiguredRunner(jobId, jobName, [], RunnerRole.Unknown, $"matrix.{key}", "The matrix is computed at run time.")];
        var values = new List<string>();
        if (Child(matrix, key) is YamlSequenceNode seq) values.AddRange(seq.Children.OfType<YamlScalarNode>().Select(c => c.Value ?? "").Where(s => s.Length > 0));
        if (Child(matrix, "include") is YamlSequenceNode inc)
            foreach (var item in inc.Children.OfType<YamlMappingNode>())
                if (Child(item, key) is YamlScalarNode { Value: { Length: > 0 } iv }) values.Add(iv);
        values = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count == 0 || values.Any(v => v.Contains("${{", StringComparison.Ordinal)))
            return [new ConfiguredRunner(jobId, jobName, [], RunnerRole.Unknown, $"matrix.{key}", "The matrix values are computed at run time.")];
        return values.Select(v => new ConfiguredRunner(jobId, jobName, [v], RunnerRole.Configured, $"matrix.{key}"));
    }

    /// <summary>Accepts a JSON array/string (what <c>fromJSON</c> would see) or a plain label.</summary>
    public static IReadOnlyList<string> ParseLabels(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('[') || v.StartsWith('"'))
        {
            try
            {
                using var doc = JsonDocument.Parse(v);
                if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList();
                if (doc.RootElement.ValueKind == JsonValueKind.String) return [doc.RootElement.GetString()!];
            }
            catch (JsonException) { }
        }
        return v.Length == 0 ? [] : [v];
    }

    private static YamlNode? Child(YamlNode? node, string key) =>
        node is YamlMappingNode m ? m.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == key).Value : null;
}
