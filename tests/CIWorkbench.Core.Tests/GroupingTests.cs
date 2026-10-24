using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

public class GroupingTests
{
    private static readonly WorkflowInfo[] Wfs =
    [
        T.Wf(".github/workflows/ci.yml", "CI"), T.Wf(".github/workflows/codeql.yml", "CodeQL"),
        T.Wf(".github/workflows/deploy-prod.yml", "Deploy"), T.Wf(".github/workflows/misc.yml", "Misc"),
        T.Wf("dynamic/github-code-scanning/codeql", "CodeQL default"),
    ];

    [Fact]
    public void Config_beats_comment_beats_default()
    {
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Security\n    workflows: [.github/workflows/codeql.yml]\n");
        var comments = new Dictionary<string, string?>
        {
            [".github/workflows/codeql.yml"] = "Maintenance",   // loses to config
            [".github/workflows/ci.yml"] = "Build & Test",       // wins over default
        };
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg, comments);
        string G(string p) => res.AllWorkflows.Single(a => a.Workflow.Path == p).GroupPath;
        Assert.Equal("Security", G(".github/workflows/codeql.yml"));
        Assert.Equal("Build & Test", G(".github/workflows/ci.yml"));
        Assert.Equal("Ungrouped", G(".github/workflows/misc.yml"));
        Assert.Equal(GroupSource.Comment, res.AllWorkflows.Single(a => a.Workflow.Path.EndsWith("ci.yml")).Source);
    }

    [Fact]
    public void Every_workflow_appears_exactly_once_even_with_overlapping_config()
    {
        var yaml = "groups:\n  - name: A\n    workflows: [\".github/workflows/*.yml\"]\n  - name: B\n    workflows: [.github/workflows/ci.yml]\n";
        var (cfg, _) = GroupConfigParser.Parse(yaml);
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        Assert.Equal(Wfs.Length, res.AllWorkflows.Count());
        Assert.Equal(Wfs.Length, res.AllWorkflows.Select(a => a.Workflow.Path).Distinct().Count());
        Assert.Contains(res.Warnings, w => w.Contains("\"B\""));
    }

    [Theory]
    [InlineData("groups: [ this is : not valid yaml")]
    [InlineData("groups: 12")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_or_missing_config_never_hides_workflows(string? yaml)
    {
        var (cfg, _) = GroupConfigParser.Parse(yaml);
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        Assert.Equal(Wfs.Length, res.AllWorkflows.Count());
    }

    [Fact]
    public void Malformed_config_produces_warning()
    {
        var (_, warning) = GroupConfigParser.Parse("groups: [ nope : :");
        Assert.NotNull(warning);
    }

    [Fact]
    public void Identity_is_path_not_display_name()
    {
        var a = T.Wf(".github/workflows/a.yml", "CI"); var b = T.Wf(".github/workflows/b.yml", "CI");
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Only A\n    workflows: [.github/workflows/a.yml]\n");
        var res = WorkflowGroupResolver.Resolve([a, b], cfg);
        Assert.Equal("Only A", res.AllWorkflows.Single(x => x.Workflow == a).GroupPath);
        Assert.Equal("Ungrouped", res.AllWorkflows.Single(x => x.Workflow == b).GroupPath);
    }

    [Fact]
    public void Nested_groups_via_slash_and_default_group_sorts_last()
    {
        var (cfg, _) = GroupConfigParser.Parse("groups:\n  - name: Build/Unit\n    workflows: [.github/workflows/ci.yml]\n");
        var res = WorkflowGroupResolver.Resolve(Wfs, cfg);
        Assert.Equal("Build", res.Groups[0].Name);
        Assert.Equal("Unit", res.Groups[0].Children.Single().Name);
        Assert.Equal("Ungrouped", res.Groups[^1].Name);
    }

    [Theory]
    [InlineData("# ci-workbench-group: Security\non: push", "Security")]
    [InlineData("name: x\n  #   CI-Workbench-Group:   \"AI Agents\"  \n", "AI Agents")]
    [InlineData("name: x\non: push", null)]
    [InlineData("# ci-workbench-group:   \n", null)]
    public void Comment_extraction(string yaml, string? expected) =>
        Assert.Equal(expected, WorkflowGroupResolver.ExtractCommentGroup(yaml));
}

