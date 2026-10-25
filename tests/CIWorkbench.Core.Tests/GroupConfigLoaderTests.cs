using System.Text.Json.Nodes;
using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

public class GroupConfigLoaderTests
{
    private static readonly RepoRef Repo = new("acme", "app");
    private const string Preferred = ".github/ciworkbench.yml";
    private const string Legacy = ".ciworkbench.yml";
    private const string PreferredYaml = "groups:\n  - name: Preferred\n    workflows: [.github/workflows/a.yml]\n";
    private const string LegacyYaml = "groups:\n  - name: Legacy\n    workflows: [.github/workflows/a.yml]\n";

    private sealed class FakeProvider(Dictionary<(RepoRef, string), string?> files, HashSet<(RepoRef, string)>? throwing = null) : ICiProvider
    {
        public List<(RepoRef Repo, string Path)> Requests { get; } = [];
        public string Name => "fake"; public bool IsDemo => false;
        public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct)
        {
            Requests.Add((repo, path));
            if (throwing?.Contains((repo, path)) == true) throw new CiProviderException("403 forbidden", 403);
            return Task.FromResult(files.TryGetValue((repo, path), out var c) ? c : null);
        }
        public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Dictionary<(RepoRef, string), string?> Files(params (string Path, string Yaml)[] f) => f.ToDictionary(x => (Repo, x.Path), x => (string?)x.Yaml);

    [Fact] public void Search_order_is_dot_github_first_then_legacy_root() =>
        Assert.Equal([Preferred, Legacy], GroupConfigContract.SearchPaths);

    [Fact] public async Task Preferred_file_is_used_when_only_it_exists()
    {
        var p = new FakeProvider(Files((Preferred, PreferredYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.Equal("Preferred", Assert.Single(r.Result.Config.Groups).Name);
        Assert.Equal([(Repo, Preferred)], p.Requests); // legacy is not even requested
    }

    [Fact] public async Task Legacy_file_still_works_when_the_preferred_file_is_missing()
    {
        var p = new FakeProvider(Files((Legacy, LegacyYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        Assert.Equal(Legacy, r.SourcePath);
        Assert.Equal("Legacy", Assert.Single(r.Result.Config.Groups).Name);
        Assert.Empty(r.Result.Diagnostics);
        Assert.Equal([Preferred, Legacy], p.Requests.Select(x => x.Path));
    }

    [Fact] public async Task Preferred_wins_over_legacy_when_both_exist()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, PreferredYaml), (Legacy, LegacyYaml))), Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.Equal("Preferred", Assert.Single(r.Result.Config.Groups).Name);
    }

    [Fact] public async Task Missing_files_mean_no_configuration_and_no_diagnostics()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider([]), Repo, default);
        Assert.Null(r.SourcePath);
        Assert.Empty(r.Result.Config.Groups);
        Assert.Empty(r.Result.Diagnostics);
    }

    [Fact] public async Task Invalid_preferred_file_is_reported_and_never_falls_back_to_legacy()
    {
        var p = new FakeProvider(Files((Preferred, "groups:\n  - name: X\n    workflow: [a]\n"), (Legacy, LegacyYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.True(r.Result.HasErrors);
        Assert.DoesNotContain(r.Result.Config.Groups, g => g.Name == "Legacy");
        Assert.DoesNotContain(p.Requests, x => x.Path == Legacy);
        Assert.StartsWith(".github/ciworkbench.yml line 3:", r.Result.Diagnostics.First(d => d.Line == 3).ToString(r.SourcePath!));
    }

    [Fact] public async Task Syntax_error_in_preferred_file_ignores_it_entirely_without_using_legacy()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, "groups: [unclosed\n"), (Legacy, LegacyYaml))), Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.Contains(r.Result.Diagnostics, d => d.Code == "syntax-error");
        Assert.Empty(r.Result.Config.Groups);
    }

    [Fact] public async Task Invalid_legacy_file_is_reported_against_the_legacy_path()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Legacy, "version: 99\n"))), Repo, default);
        Assert.Equal(Legacy, r.SourcePath);
        Assert.True(r.Result.HasErrors);
        Assert.StartsWith(".ciworkbench.yml line 1:", r.Result.Diagnostics[0].ToString(r.SourcePath!));
    }

    [Fact] public async Task Empty_preferred_file_counts_as_existing_and_hides_the_legacy_file()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, "# nothing yet\n"), (Legacy, LegacyYaml))), Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.Empty(r.Result.Config.Groups);
    }

    [Fact] public async Task Unreadable_preferred_file_warns_and_does_not_fall_back()
    {
        var p = new FakeProvider(Files((Legacy, LegacyYaml)), [(Repo, Preferred)]);
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        Assert.Equal(Preferred, r.SourcePath);
        Assert.Empty(r.Result.Config.Groups);
        Assert.Contains(r.Result.Diagnostics, d => d.Code == "unreadable" && d.Severity == ConfigSeverity.Warning);
        Assert.False(r.Result.HasErrors);
    }

    [Fact] public async Task Unreadable_legacy_file_warns_after_a_missing_preferred_file()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider([], [(Repo, Legacy)]), Repo, default);
        Assert.Equal(Legacy, r.SourcePath);
        Assert.Contains(r.Result.Diagnostics, d => d.Code == "unreadable");
    }

    [Fact] public async Task Only_the_selected_repository_is_queried_and_only_the_two_config_paths()
    {
        var other = new RepoRef("acme", "other");
        var files = Files((Preferred, PreferredYaml));
        files[(other, Preferred)] = "groups:\n  - name: Other\n";
        var p = new FakeProvider(files);
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        Assert.Equal("Preferred", r.Result.Config.Groups[0].Name);
        Assert.All(p.Requests, x => Assert.Equal(Repo, x.Repo));
        Assert.All(p.Requests, x => Assert.Contains(x.Path, GroupConfigContract.SearchPaths));
    }

    [Fact] public async Task Same_contract_applies_to_both_locations()
    {
        var a = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, PreferredYaml))), Repo, default);
        var b = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Legacy, PreferredYaml))), Repo, default);
        Assert.Equal(a.Result.Config.Groups.Select(g => (g.Name, string.Join(",", g.Workflows))), b.Result.Config.Groups.Select(g => (g.Name, string.Join(",", g.Workflows))));
        Assert.Equal(a.Result.Diagnostics.Select(d => d.Message), b.Result.Diagnostics.Select(d => d.Message));
    }

    [Fact] public void Diagnostics_keep_the_legacy_file_name_by_default_and_name_the_source_on_request()
    {
        var d = new ConfigDiagnostic(ConfigSeverity.Error, "x", "boom", 4);
        Assert.Equal(".ciworkbench.yml line 4: boom", d.ToString());
        Assert.Equal(".github/ciworkbench.yml line 4: boom", d.ToString(Preferred));
    }

    [Fact] public void Published_schema_is_location_independent()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "ciworkbench.schema.json"));
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Contains(".github/ciworkbench.yml", root["title"]!.GetValue<string>());
        Assert.Equal(GroupConfigContract.SchemaUrl, root["$id"]!.GetValue<string>());
    }
}
