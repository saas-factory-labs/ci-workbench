using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class GroupingTests
{
    private static readonly WorkflowInfo[] Wfs =
    [
        T.Wf(".github/workflows/ci.yml", "CI"), T.Wf(".github/workflows/codeql.yml", "CodeQL"),
        T.Wf(".github/workflows/deploy-prod.yml", "Deploy"), T.Wf(".github/workflows/misc.yml", "Misc"),
        T.Wf("dynamic/github-code-scanning/codeql", "CodeQL default"),
    ];

    [Test]
    public async Task Config_beats_comment_beats_default()
    {
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Security\n    workflows: [.github/workflows/codeql.yml]\n");
        var comments = new Dictionary<string, string?>
        {
            [".github/workflows/codeql.yml"] = "Maintenance",   // loses to config
            [".github/workflows/ci.yml"] = "Build & Test",       // wins over default
        };
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg, comments);
        string G(string p) => res.AllWorkflows.Single(a => a.Workflow.Path == p).GroupPath;
        await Assert.That(G(".github/workflows/codeql.yml")).IsEqualTo("Security");
        await Assert.That(G(".github/workflows/ci.yml")).IsEqualTo("Build & Test");
        await Assert.That(G(".github/workflows/misc.yml")).IsEqualTo("Ungrouped");
        await Assert.That(res.AllWorkflows.Single(a => a.Workflow.Path.EndsWith("ci.yml")).Source).IsEqualTo(GroupSource.Comment);
    }

    [Test]
    public async Task Every_workflow_appears_exactly_once_even_with_overlapping_config()
    {
        var yaml = "groups:\n  - name: A\n    workflows: [\".github/workflows/*.yml\"]\n  - name: B\n    workflows: [.github/workflows/ci.yml]\n";
        var (cfg, _) = GroupConfigParser.Parse(yaml);
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        await Assert.That(res.AllWorkflows.Count()).IsEqualTo(Wfs.Length);
        await Assert.That(res.AllWorkflows.Select(a => a.Workflow.Path).Distinct().Count()).IsEqualTo(Wfs.Length);
        await Assert.That(res.Warnings).Contains(w => w.Contains("\"B\""));
    }

    [Test]
    [Arguments("groups: [ this is : not valid yaml")]
    [Arguments("groups: 12")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Malformed_or_missing_config_never_hides_workflows(string? yaml)
    {
        var (cfg, _) = GroupConfigParser.Parse(yaml);
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        await Assert.That(res.AllWorkflows.Count()).IsEqualTo(Wfs.Length);
    }

    [Test]
    public async Task Malformed_config_produces_warning()
    {
        var (_, warning) = GroupConfigParser.Parse("groups: [ nope : :");
        await Assert.That(warning).IsNotNull();
    }

    [Test]
    public async Task Identity_is_path_not_display_name()
    {
        var a = T.Wf(".github/workflows/a.yml", "CI"); var b = T.Wf(".github/workflows/b.yml", "CI");
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Only A\n    workflows: [.github/workflows/a.yml]\n");
        var res = WorkflowGroupResolver.Resolve([a, b], cfg);
        await Assert.That(res.AllWorkflows.Single(x => x.Workflow == a).GroupPath).IsEqualTo("Only A");
        await Assert.That(res.AllWorkflows.Single(x => x.Workflow == b).GroupPath).IsEqualTo("Ungrouped");
    }

    [Test]
    public async Task Nested_groups_via_slash_and_default_group_sorts_last()
    {
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Build/Unit\n    workflows: [.github/workflows/ci.yml]\n");
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        await Assert.That(res.Groups[0].Name).IsEqualTo("Build");
        await Assert.That(res.Groups[0].Children.Single().Name).IsEqualTo("Unit");
        await Assert.That(res.Groups[^1].Name).IsEqualTo("Ungrouped");
    }

    [Test]
    [Arguments("# ci-workbench-group: Security\non: push", "Security")]
    [Arguments("name: x\n  #   CI-Workbench-Group:   \"AI Agents\"  \n", "AI Agents")]
    [Arguments("name: x\non: push", null)]
    [Arguments("# ci-workbench-group:   \n", null)]
    public async Task Comment_extraction(string yaml, string? expected) =>
        await Assert.That(WorkflowGroupResolver.ExtractCommentGroup(yaml)).IsEqualTo(expected);
}
