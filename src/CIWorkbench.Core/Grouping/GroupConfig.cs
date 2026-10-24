namespace CIWorkbench.Core.Grouping;

/// <summary>
/// The published contract of <c>.github/ciworkbench.yml</c> (or the legacy root <c>.ciworkbench.yml</c>). <c>schemas/ciworkbench.schema.json</c> describes the same contract for editors;
/// the patterns and limits below are repeated verbatim there, and a unit test fails if the two drift apart.
/// </summary>
public static class GroupConfigContract
{
    /// <summary>Preferred location: the repository's <c>.github</c> folder.</summary>
    public const string PreferredPath = ".github/ciworkbench.yml";

    /// <summary>Legacy location (repository root), still read when the preferred file does not exist.</summary>
    public const string LegacyPath = ".ciworkbench.yml";

    /// <summary>Lookup order; the first file that exists is used.</summary>
    public static readonly IReadOnlyList<string> SearchPaths = [PreferredPath, LegacyPath];

    /// <summary>Default file name used in diagnostics when the source file is not known.</summary>
    public const string FileName = LegacyPath;
    public const int CurrentVersion = 1;

    /// <summary>The public schema URL (GitHub Raw, default branch). It only resolves once the schema is merged to that branch.</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/saas-factory-labs/ci-workbench/main/schemas/ciworkbench.schema.json";

    public const string SchemaDirective = "# yaml-language-server: $schema=" + SchemaUrl;

    /// <summary>No leading/trailing '/' or whitespace and no '//' (empty path segments).</summary>
    public const string GroupNamePattern = @"^(?!.*//)[^/\s](?:.*[^/\s])?$";

    /// <summary>No whitespace, '**', '?', '[', ']', '{' or '}'. A single '*' (within one path segment) is the only wildcard.</summary>
    public const string WorkflowPattern = @"^(?!.*\*\*)[^\s?\[\]{}]+$";

    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 500;
    public const int MaxWorkflowPatternLength = 260;

    public static readonly IReadOnlyList<string> RootFields = ["version", "groups"];
    public static readonly IReadOnlyList<string> GroupFields = ["name", "description", "workflows"];
}

public enum ConfigSeverity
{
    /// <summary>A violation of the published contract (the JSON Schema also rejects it). The affected part is not applied.</summary>
    Error,
    /// <summary>Valid but suspicious: needs the repository's workflows or compares entries to each other (not expressible in JSON Schema).</summary>
    Warning,
}

/// <param name="Code">Stable machine-readable identifier, e.g. <c>unknown-field</c>.</param>
public sealed record ConfigDiagnostic(ConfigSeverity Severity, string Code, string Message, int? Line = null)
{
    public static string Format(int? line, string message, string fileName = GroupConfigContract.FileName) => $"{fileName}{(line is { } l ? $" line {l}" : "")}: {message}";

    /// <summary>User-facing text, e.g. ".ciworkbench.yml line 7: unknown field 'workflow' (did you mean 'workflows'?)".</summary>
    public override string ToString() => Format(Line, Message);

    /// <summary>Same text, naming the file the configuration was actually read from.</summary>
    public string ToString(string fileName) => Format(Line, Message, fileName);
}

public sealed class GroupConfigEntry
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Workflows { get; set; } = [];
    /// <summary>1-based YAML line of the group, or null for programmatically built entries.</summary>
    public int? Line { get; set; }
    /// <summary>YAML line of each pattern in <see cref="Workflows"/> (same order); may be shorter or empty.</summary>
    public List<int> WorkflowLines { get; set; } = [];

    public int? LineOfPattern(int index) => index < WorkflowLines.Count ? WorkflowLines[index] : Line;
}

public sealed class GroupConfig
{
    public int Version { get; set; } = GroupConfigContract.CurrentVersion;
    public List<GroupConfigEntry> Groups { get; set; } = [];
}

public sealed class GroupConfigParseResult(GroupConfig config, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public GroupConfig Config { get; } = config;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool HasErrors => Diagnostics.Any(d => d.Severity == ConfigSeverity.Error);

    /// <summary>All diagnostics as one string, or null when the file is clean (kept for callers that only need a single message).</summary>
    public string? Warning => Diagnostics.Count == 0 ? null : string.Join(" ", Diagnostics.Select(d => d.ToString()));

    public void Deconstruct(out GroupConfig config, out string? warning) { config = Config; warning = Warning; }
}
