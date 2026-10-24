using System.Text.RegularExpressions;
using CIWorkbench.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CIWorkbench.Core.Grouping;

public enum GroupSource { Config, Comment, Default }

public sealed record AssignedWorkflow(WorkflowInfo Workflow, string GroupPath, GroupSource Source);

public sealed class WorkflowGroup
{
    public required string Name { get; init; }
    /// <summary>Slash-separated full path, e.g. "Build &amp; Test/Unit".</summary>
    public required string Path { get; init; }
    public string? Description { get; set; }
    public List<AssignedWorkflow> Workflows { get; } = [];
    public List<WorkflowGroup> Children { get; } = [];
    public int TotalWorkflows => Workflows.Count + Children.Sum(c => c.TotalWorkflows);
    public IEnumerable<AssignedWorkflow> AllWorkflows => Workflows.Concat(Children.SelectMany(c => c.AllWorkflows));
}

public sealed record GroupingResult(IReadOnlyList<WorkflowGroup> Groups, IReadOnlyList<string> Warnings)
{
    public IEnumerable<AssignedWorkflow> AllWorkflows => Groups.SelectMany(g => g.AllWorkflows);
}

public sealed class GroupConfigEntry
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Workflows { get; set; } = [];
}

public sealed class GroupConfig
{
    public List<GroupConfigEntry> Groups { get; set; } = [];
}

public static class GroupConfigParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Never throws: malformed config yields an empty config plus a warning.</summary>
    public static (GroupConfig Config, string? Warning) Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return (new GroupConfig(), null);
        try
        {
            var cfg = Yaml.Deserialize<GroupConfig?>(yaml) ?? new GroupConfig();
            cfg.Groups = (cfg.Groups ?? []).Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToList();
            foreach (var g in cfg.Groups) g.Workflows ??= [];
            return (cfg, null);
        }
        catch (Exception ex)
        {
            return (new GroupConfig(), $".ciworkbench.yml could not be parsed and was ignored: {ex.Message.Split('\n')[0].Trim()}");
        }
    }
}

public static partial class WorkflowGroupResolver
{
    public const string DefaultGroupName = "Ungrouped";

    [GeneratedRegex(@"^\s*#\s*ci-workbench-group:\s*(?<g>.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CommentRegex();

    /// <summary>Reads the first <c># ci-workbench-group: X</c> comment. Raw-text scan because YAML parsers drop comments.</summary>
    public static string? ExtractCommentGroup(string? workflowYaml)
    {
        if (string.IsNullOrEmpty(workflowYaml)) return null;
        var m = CommentRegex().Match(workflowYaml);
        var g = m.Success ? m.Groups["g"].Value.Trim().Trim('"', '\'') : null;
        return string.IsNullOrWhiteSpace(g) ? null : g;
    }

    /// <summary>
    /// Precedence: central config, then workflow comment, then default. Every workflow lands in exactly one group.
    /// </summary>
    public static GroupingResult Resolve(
        IReadOnlyList<WorkflowInfo> workflows,
        GroupConfig? config,
        IReadOnlyDictionary<string, string?>? commentGroupsByPath = null,
        IEnumerable<string>? extraWarnings = null)
    {
        var warnings = new List<string>(extraWarnings ?? []);
        var assignments = new List<(WorkflowInfo W, string Path, GroupSource Src)>();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var g in config?.Groups ?? [])
        {
            var p = NormalizePath(g.Name);
            if (p.Length == 0) continue;
            if (!order.Contains(p, StringComparer.OrdinalIgnoreCase)) order.Add(p);
            if (!string.IsNullOrWhiteSpace(g.Description)) descriptions[p] = g.Description!;
        }

        foreach (var w in workflows.OrderBy(w => w.Path, StringComparer.OrdinalIgnoreCase))
        {
            string? path = null; var src = GroupSource.Default;
            foreach (var g in config?.Groups ?? [])
            {
                if (g.Workflows.Any(pat => PathMatches(pat, w.Path))) { path = NormalizePath(g.Name); src = GroupSource.Config; break; }
            }
            if (path is null && commentGroupsByPath is not null && commentGroupsByPath.TryGetValue(w.Path, out var cg) && !string.IsNullOrWhiteSpace(cg))
            { path = NormalizePath(cg); src = GroupSource.Comment; }
            if (string.IsNullOrEmpty(path)) { path = DefaultGroupName; src = GroupSource.Default; }
            assignments.Add((w, path, src));
        }

        foreach (var g in config?.Groups ?? [])
        {
            var p = NormalizePath(g.Name);
            if (p.Length > 0 && !assignments.Any(a => a.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"Group \"{p}\" in .ciworkbench.yml matched no workflows.");
        }

        var roots = new List<WorkflowGroup>();
        var index = new Dictionary<string, WorkflowGroup>(StringComparer.OrdinalIgnoreCase);

        WorkflowGroup Ensure(string fullPath)
        {
            if (index.TryGetValue(fullPath, out var existing)) return existing;
            var i = fullPath.LastIndexOf('/');
            var g = new WorkflowGroup { Name = i < 0 ? fullPath : fullPath[(i + 1)..], Path = fullPath };
            if (descriptions.TryGetValue(fullPath, out var d)) g.Description = d;
            index[fullPath] = g;
            if (i < 0) roots.Add(g); else Ensure(fullPath[..i]).Children.Add(g);
            return g;
        }

        // Configured groups appear in config order even when empty-of-direct-workflows; empty ones are dropped below.
        foreach (var p in order) Ensure(p);
        foreach (var (w, path, src) in assignments) Ensure(path).Workflows.Add(new AssignedWorkflow(w, path, src));

        static void Prune(List<WorkflowGroup> list) { list.RemoveAll(g => g.TotalWorkflows == 0); foreach (var g in list) Prune(g.Children); }
        Prune(roots);

        var configOrder = order.Select(o => o.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sorted = roots
            .OrderBy(g => g.Name.Equals(DefaultGroupName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(g => { var i = configOrder.FindIndex(c => c.Equals(g.Name, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; })
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new GroupingResult(sorted, warnings);
    }

    private static string NormalizePath(string name) =>
        string.Join('/', name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Exact (case-insensitive) match or simple <c>*</c> wildcard match on the workflow path.</summary>
    public static bool PathMatches(string pattern, string path)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        pattern = pattern.Trim().Replace('\\', '/');
        if (!pattern.Contains('*')) return pattern.Equals(path, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(path, rx, RegexOptions.IgnoreCase);
    }
}

