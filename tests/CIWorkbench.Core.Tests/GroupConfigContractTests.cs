using TUnit.Assertions.Enums;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

/// <summary>
/// The published JSON Schema (schemas/ciworkbench.schema.json) and the runtime parser must describe one contract.
/// Every case below is run through BOTH; "valid" means: the schema accepts it and the parser reports no Error.
/// </summary>
public class GroupConfigContractTests
{
    public sealed record Case(string Id, string Yaml, bool Valid, string? Code = null)
    {
        public override string ToString() => Id;
    }

    private static string Long(int n) => new('x', n);

    public static IEnumerable<object[]> Cases() => Corpus.Select(c => new object[] { c });

    public static readonly Case[] Corpus =
    [
        // ---------------------------------------------------------------- valid
        new("empty-file", "", true),
        new("comments-only", "# nothing configured yet\n", true),
        new("no-groups", "version: 1\n", true),
        new("empty-groups-list", "groups: []\n", true),
        new("version-omitted-is-v1", "groups:\n  - name: CI\n    workflows: [.github/workflows/ci.yml]\n", true),
        new("full-example", "version: 1\ngroups:\n  - name: Build & Test\n    description: Compile and test\n    workflows:\n      - .github/workflows/ci.yml\n      - .github/workflows/*-tests.yml\n  - name: Security/Scanning\n    workflows: [.github/workflows/codeql.yml]\n", true),
        new("name-only", "groups:\n  - name: Security\n", true),
        new("empty-workflows-list", "groups:\n  - name: AI Agents\n    description: filled by workflow comments\n    workflows: []\n", true),
        new("numeric-looking-name-quoted", "groups:\n  - name: \"2024\"\n", true),
        new("unicode-name", "groups:\n  - name: Bygge & Test æøå\n", true),
        new("name-with-spaces-around-slash", "groups:\n  - name: A / B\n", true),
        new("max-length-name", $"groups:\n  - name: {Long(200)}\n", true),
        new("max-length-description", $"groups:\n  - name: A\n    description: {Long(500)}\n", true),
        new("wildcard-in-segment", "groups:\n  - name: A\n    workflows: ['.github/workflows/deploy-*.yml', '*']\n", true),
        // valid per schema; the parser adds WARNINGS only (semantic checks)
        new("duplicate-group-names", "groups:\n  - name: A\n  - name: a\n", true, "duplicate-group"),
        new("same-pattern-in-two-groups", "groups:\n  - name: A\n    workflows: [x.yml]\n  - name: B\n    workflows: [x.yml]\n", true, "duplicate-pattern"),
        new("same-pattern-twice", "groups:\n  - name: A\n    workflows: [x.yml, x.yml]\n", true, "duplicate-pattern"),

        // ---------------------------------------------------------------- invalid: structure
        new("syntax-error", "groups: [ this is : not valid yaml", false, "syntax-error"),
        new("root-is-list", "- a\n- b\n", false, "invalid-type"),
        new("root-is-scalar", "hello\n", false, "invalid-type"),
        new("unknown-root-field", "groupz: []\n", false, "unknown-field"),
        new("unknown-root-field-next-to-valid", "version: 1\nfoo: bar\ngroups: []\n", false, "unknown-field"),
        new("unknown-group-field-typo", "groups:\n  - name: A\n    workflow: [x.yml]\n", false, "unknown-field"),
        new("groups-is-mapping", "groups:\n  name: A\n", false, "invalid-type"),
        new("groups-is-null", "groups:\n", false, "invalid-type"),
        new("group-entry-is-scalar", "groups:\n  - CI\n", false, "invalid-type"),
        new("group-entry-is-list", "groups:\n  - [a, b]\n", false, "invalid-type"),
        // ---------------------------------------------------------------- invalid: version
        new("version-string", "version: \"1\"\ngroups: []\n", false, "invalid-type"),
        new("version-float", "version: 1.5\n", false, "invalid-type"),
        new("version-bool", "version: true\n", false, "invalid-type"),
        new("version-null", "version:\n", false, "invalid-type"),
        new("version-from-the-future", "version: 2\ngroups: []\n", false, "unsupported-version"),
        new("version-zero", "version: 0\n", false, "unsupported-version"),
        // ---------------------------------------------------------------- invalid: name
        new("missing-name", "groups:\n  - workflows: [x.yml]\n", false, "missing-field"),
        new("name-number", "groups:\n  - name: 123\n", false, "invalid-type"),
        new("name-bool", "groups:\n  - name: true\n", false, "invalid-type"),
        new("name-null", "groups:\n  - name:\n", false, "invalid-type"),
        new("name-empty-string", "groups:\n  - name: \"\"\n", false, "invalid-value"),
        new("name-whitespace-only", "groups:\n  - name: \"   \"\n", false, "invalid-value"),
        new("name-leading-slash", "groups:\n  - name: /A\n", false, "invalid-value"),
        new("name-trailing-slash", "groups:\n  - name: A/\n", false, "invalid-value"),
        new("name-double-slash", "groups:\n  - name: A//B\n", false, "invalid-value"),
        new("name-leading-space", "groups:\n  - name: \" A\"\n", false, "invalid-value"),
        new("name-trailing-space", "groups:\n  - name: \"A \"\n", false, "invalid-value"),
        new("name-too-long", $"groups:\n  - name: {Long(201)}\n", false, "invalid-value"),
        // ---------------------------------------------------------------- invalid: description
        new("description-number", "groups:\n  - name: A\n    description: 5\n", false, "invalid-type"),
        new("description-list", "groups:\n  - name: A\n    description: [a]\n", false, "invalid-type"),
        new("description-too-long", $"groups:\n  - name: A\n    description: {Long(501)}\n", false, "invalid-value"),
        // ---------------------------------------------------------------- invalid: workflows
        new("workflows-string", "groups:\n  - name: A\n    workflows: .github/workflows/ci.yml\n", false, "invalid-type"),
        new("workflows-null", "groups:\n  - name: A\n    workflows:\n", false, "invalid-type"),
        new("workflows-item-number", "groups:\n  - name: A\n    workflows: [1]\n", false, "invalid-type"),
        new("workflows-item-null", "groups:\n  - name: A\n    workflows:\n      -\n", false, "invalid-type"),
        new("workflows-item-mapping", "groups:\n  - name: A\n    workflows:\n      - path: x.yml\n", false, "invalid-type"),
        new("workflows-item-empty", "groups:\n  - name: A\n    workflows: [\"\"]\n", false, "invalid-value"),
        new("workflows-double-star", "groups:\n  - name: A\n    workflows: ['.github/**/*.yml']\n", false, "invalid-value"),
        new("workflows-question-mark", "groups:\n  - name: A\n    workflows: ['ci?.yml']\n", false, "invalid-value"),
        new("workflows-char-class", "groups:\n  - name: A\n    workflows: ['ci[12].yml']\n", false, "invalid-value"),
        new("workflows-brace", "groups:\n  - name: A\n    workflows: ['ci.{yml,yaml}']\n", false, "invalid-value"),
        new("workflows-whitespace", "groups:\n  - name: A\n    workflows: ['my workflow.yml']\n", false, "invalid-value"),
        new("workflows-item-too-long", $"groups:\n  - name: A\n    workflows: [{Long(261)}]\n", false, "invalid-value"),
    ];

    // ------------------------------------------------------------------ helpers

    private static string ContractFile(string name) => Path.Combine(AppContext.BaseDirectory, "contract", name);
    // The library registers a schema's $id globally, so it may only be built once per process.
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(ContractFile("ciworkbench.schema.json"))));
    private static JsonSchema LoadSchema() => Schema.Value;

    /// <summary>
    /// A deliberately separate, minimal YAML-to-JSON conversion (YAML 1.2 core typing: quoted = text, plain true/false/null/numbers are typed)
    /// so the schema is evaluated on what a real YAML tool would see, not on what the runtime parser decided.
    /// </summary>
    private static JsonNode? YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0) return new JsonObject();             // empty or comment-only file = "no configuration"
        var root = stream.Documents[0].RootNode;
        return root is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" } ? new JsonObject() : Convert(root);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, Convert(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(Convert).ToArray()),
        YamlScalarNode sc => Scalar(sc),
        _ => throw new NotSupportedException(),
    };

    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style != ScalarStyle.Plain) return JsonValue.Create(v);
        if (v is "" or "~" or "null" or "Null" or "NULL") return null;
        if (v.ToLowerInvariant() is "true" or "false") return JsonValue.Create(v.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (long.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
    private static bool SchemaAccepts(JsonSchema schema, string yaml)
    {
        JsonNode? node;
        try { node = YamlToJson(yaml); }
        catch (Exception) { return false; }                        // not even YAML: no schema can accept it
        return schema.Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(node), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    // ------------------------------------------------------------------ tests

    [Test, MethodDataSource(nameof(Cases))]
    public async Task Runtime_validation_and_published_schema_agree(Case c)
    {
        var schemaSays = SchemaAccepts(LoadSchema(), c.Yaml);
        var parsed = GroupConfigParser.Parse(c.Yaml);
        var runtimeSays = !parsed.HasErrors;

        await Assert.That(schemaSays == c.Valid).IsTrue().Because($"[{c.Id}] published schema says valid={schemaSays}, contract expects {c.Valid}");
        await Assert.That(runtimeSays == c.Valid).IsTrue().Because($"[{c.Id}] runtime parser says valid={runtimeSays}, contract expects {c.Valid}. Diagnostics: {string.Join(" | ", parsed.Diagnostics)}");
    }

    [Test, MethodDataSource(nameof(Cases))]
    public async Task Every_problem_has_a_stable_code_a_message_and_a_line(Case c)
    {
        var parsed = GroupConfigParser.Parse(c.Yaml);
        if (c.Code is null) { await Assert.That(parsed.Diagnostics).IsEmpty(); return; }
        var d = Enumerable.Single(parsed.Diagnostics, x => x.Code == c.Code);
        await Assert.That(string.IsNullOrWhiteSpace(d.Message)).IsFalse();
        await Assert.That(d.Severity).IsEqualTo(c.Valid ? ConfigSeverity.Warning : ConfigSeverity.Error);
        if (c.Id != "empty-file") await Assert.That(d.Line is > 0).IsTrue().Because($"[{c.Id}] diagnostic has no line number");
        await Assert.That(d.ToString()).StartsWith(".ciworkbench.yml line ");
    }

    [Test]
    public async Task Patterns_and_limits_in_the_schema_are_the_ones_the_runtime_uses()
    {
        var root = JsonNode.Parse(File.ReadAllText(ContractFile("ciworkbench.schema.json")))!;
        var group = root["$defs"]!["group"]!["properties"]!;
        await Assert.That((string)group["name"]!["pattern"]!).IsEqualTo(GroupConfigContract.GroupNamePattern);
        await Assert.That((string)group["workflows"]!["items"]!["pattern"]!).IsEqualTo(GroupConfigContract.WorkflowPattern);
        await Assert.That((int)group["name"]!["maxLength"]!).IsEqualTo(GroupConfigContract.MaxNameLength);
        await Assert.That((int)group["description"]!["maxLength"]!).IsEqualTo(GroupConfigContract.MaxDescriptionLength);
        await Assert.That((int)group["workflows"]!["items"]!["maxLength"]!).IsEqualTo(GroupConfigContract.MaxWorkflowPatternLength);
        await Assert.That((int)root["properties"]!["version"]!["enum"]![0]!).IsEqualTo(GroupConfigContract.CurrentVersion);
        await Assert.That(((JsonObject)root["properties"]!).Select(p => p.Key).OrderBy(x => x)).IsEquivalentTo(GroupConfigContract.RootFields.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(((JsonObject)group).Select(p => p.Key).OrderBy(x => x)).IsEquivalentTo(GroupConfigContract.GroupFields.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That((bool)root["additionalProperties"]!).IsFalse();
        await Assert.That((bool)root["$defs"]!["group"]!["additionalProperties"]!).IsFalse();
        await Assert.That(root["$defs"]!["group"]!["required"]!.AsArray().Select(x => (string)x!)).IsEquivalentTo(["name"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Schema_id_is_the_documented_public_url_and_the_schema_is_itself_valid_draft_2020_12()
    {
        var root = JsonNode.Parse(File.ReadAllText(ContractFile("ciworkbench.schema.json")))!;
        await Assert.That((string)root["$id"]!).IsEqualTo(GroupConfigContract.SchemaUrl);
        await Assert.That((string)root["$schema"]!).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
        await Assert.That(GroupConfigContract.SchemaUrl).StartsWith("https://raw.githubusercontent.com/");
        await Assert.That(GroupConfigContract.SchemaUrl).EndsWith("/schemas/ciworkbench.schema.json");
        var schemaExamples = root["examples"]!.AsArray();
        await Assert.That(schemaExamples).IsNotEmpty();
        var schema = LoadSchema();
        foreach (var example in schemaExamples) await Assert.That(schema.Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(example), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid).IsTrue();
    }

    [Test]
    public async Task The_repositorys_own_config_uses_the_published_directive_and_is_clean()
    {
        var yaml = File.ReadAllText(ContractFile("repo.ciworkbench.yml"));
        await Assert.That(yaml).Contains(GroupConfigContract.SchemaDirective);
        await Assert.That(SchemaAccepts(LoadSchema(), yaml)).IsTrue();
        await Assert.That(GroupConfigParser.Parse(yaml).Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("README.md")]
    [Arguments("workflow-grouping.md")]
    public async Task Documented_examples_with_the_schema_directive_are_valid_and_clean(string doc)
    {
        var text = File.ReadAllText(ContractFile(doc));
        await Assert.That(text).Contains(GroupConfigContract.SchemaDirective);                         // the directive is documented verbatim
        var blocks = Regex.Matches(text, "```yaml\\r?\\n(?<y>.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups["y"].Value).Where(y => y.Contains(GroupConfigContract.SchemaDirective)).ToList();
        await Assert.That(blocks).IsNotEmpty();
        foreach (var yaml in blocks)
        {
            await Assert.That(SchemaAccepts(LoadSchema(), yaml)).IsTrue().Because($"documented example is rejected by the schema in {doc}");
            await Assert.That(GroupConfigParser.Parse(yaml).Diagnostics).IsEmpty();
        }
    }

    // ------------------------------------------------------------------ messages users actually read

    [Test]
    public async Task Typo_in_a_field_name_gets_a_suggestion_and_the_line()
    {
        var d = Enumerable.Single(GroupConfigParser.Parse("groups:\n  - name: A\n    workflow: [x.yml]\n").Diagnostics, x => x.Code == "unknown-field");
        await Assert.That(d.Line).IsEqualTo(3);
        await Assert.That(d.Message).Contains("did you mean 'workflows'");
    }

    [Test]
    public async Task A_group_with_errors_is_skipped_and_the_skip_is_reported()
    {
        var yaml = "groups:\n  - name: Good\n    workflows: [a.yml]\n  - name: Bad\n    workflow: [b.yml]\n";
        var r = GroupConfigParser.Parse(yaml);
        await Assert.That(r.Config.Groups.Select(g => g.Name)).IsEquivalentTo(["Good"], CollectionOrdering.Matching);
        await Assert.That(r.Diagnostics).Contains(d => d.Code == "group-skipped" && d.Message.Contains("'Bad'"));
    }

    [Test]
    public async Task Several_problems_are_all_reported_in_one_pass()
    {
        var r = GroupConfigParser.Parse("version: 1\nextra: 1\ngroups:\n  - name: 5\n  - workflows: [x.yml]\n");
        await Assert.That(r.Diagnostics.Count(d => d.Severity == ConfigSeverity.Error)).IsEqualTo(3);
    }

    [Test]
    public async Task An_unsupported_version_applies_nothing_and_says_so()
    {
        var r = GroupConfigParser.Parse("version: 2\ngroups:\n  - name: A\n    workflows: [a.yml]\n");
        await Assert.That(r.Config.Groups).IsEmpty();
        await Assert.That(r.Diagnostics).Contains(d => d.Code == "unsupported-version" && d.Message.Contains("supported: 1"));
    }

    [Test]
    public async Task Missing_version_is_treated_as_version_1_without_a_diagnostic()
    {
        var r = GroupConfigParser.Parse("groups:\n  - name: A\n");
        await Assert.That(r.Config.Version).IsEqualTo(1);
        await Assert.That(r.Diagnostics).IsEmpty();
    }
}

public class GroupConfigResolverContractTests
{
    private static readonly WorkflowInfo[] Wfs =
    [
        T.Wf(".github/workflows/ci.yml", "CI"), T.Wf(".github/workflows/ci-docs.yml", "Docs"),
        T.Wf(".github/workflows/codeql.yml", "CodeQL"), T.Wf(".github/workflows/misc.yml", "Misc"),
    ];

    private static GroupingResult Resolve(string yaml, IReadOnlyDictionary<string, string?>? comments = null) =>
        WorkflowGroupResolver.Resolve(Wfs, GroupConfigParser.Parse(yaml).Config, comments);

    [Test]
    public async Task Unmatched_exact_path_is_reported_with_its_line()
    {
        var r = Resolve("groups:\n  - name: A\n    workflows:\n      - .github/workflows/ci.yml\n      - .github/workflows/does-not-exist.yml\n");
        await Assert.That(r.Warnings).Contains(w => w.StartsWith(".ciworkbench.yml line 5:") && w.Contains("workflow path") && w.Contains("does-not-exist.yml") && w.Contains("matches no workflow"));
    }

    [Test]
    public async Task Unmatched_wildcard_is_reported_as_a_pattern()
    {
        var r = Resolve("groups:\n  - name: A\n    workflows: ['.github/workflows/deploy-*.yml']\n");
        await Assert.That(r.Warnings).Contains(w => w.Contains("pattern '.github/workflows/deploy-*.yml'") && w.Contains("matches no workflow"));
    }

    [Test]
    public async Task A_workflow_matched_by_two_groups_is_reported_and_the_first_group_wins()
    {
        var r = Resolve("groups:\n  - name: First\n    workflows: [.github/workflows/ci*.yml]\n  - name: Second\n    workflows: [.github/workflows/ci.yml]\n");
        await Assert.That(r.AllWorkflows.Single(a => a.Workflow.Path.EndsWith("/ci.yml")).GroupPath).IsEqualTo("First");
        await Assert.That(r.Warnings).Contains(w => w.Contains("matches several groups") && w.Contains("'First', 'Second'") && w.Contains("first group in the file wins"));
    }

    [Test]
    public async Task Precedence_is_config_then_comment_then_ungrouped()
    {
        var comments = new Dictionary<string, string?> { [".github/workflows/ci.yml"] = "FromComment", [".github/workflows/codeql.yml"] = "FromComment" };
        var r = Resolve("groups:\n  - name: FromConfig\n    workflows: [.github/workflows/ci.yml]\n", comments);
        string G(string p) => r.AllWorkflows.Single(a => a.Workflow.Path == p).GroupPath;
        await Assert.That(G(".github/workflows/ci.yml")).IsEqualTo("FromConfig");
        await Assert.That(G(".github/workflows/codeql.yml")).IsEqualTo("FromComment");
        await Assert.That(G(".github/workflows/misc.yml")).IsEqualTo("Ungrouped");
    }

    [Test, MethodDataSource(typeof(GroupConfigContractTests), nameof(GroupConfigContractTests.Cases))]
    public async Task No_configuration_valid_or_not_can_hide_a_workflow(GroupConfigContractTests.Case c)
    {
        var r = Resolve(c.Yaml);
        await Assert.That(r.AllWorkflows.Count()).IsEqualTo(Wfs.Length);
        await Assert.That(r.AllWorkflows.Select(a => a.Workflow.Path).Order()).IsEquivalentTo(Wfs.Select(w => w.Path).Order(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Wildcard_does_not_cross_path_segments()
    {
        await Assert.That(WorkflowGroupResolver.PathMatches(".github/workflows/ci-*.yml", ".github/workflows/ci-docs.yml")).IsTrue();
        await Assert.That(WorkflowGroupResolver.PathMatches(".github/*.yml", ".github/workflows/ci.yml")).IsFalse();
        await Assert.That(WorkflowGroupResolver.PathMatches(".GitHub/Workflows/CI.yml", ".github/workflows/ci.yml")).IsTrue();
    }
}