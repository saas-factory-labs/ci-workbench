using TUnit.Assertions.Enums;
using System.IO;
using System.Text.Json.Nodes;
using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;
using System.Threading.Tasks;

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

    [Test] public async Task Search_order_is_dot_github_first_then_legacy_root() =>
        await Assert.That(GroupConfigContract.SearchPaths).IsEquivalentTo([Preferred, Legacy], CollectionOrdering.Matching);

    [Test] public async Task Preferred_file_is_used_when_only_it_exists()
    {
        var p = new FakeProvider(Files((Preferred, PreferredYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(Enumerable.Single(r.Result.Config.Groups).Name).IsEqualTo("Preferred");
        await Assert.That(p.Requests).IsEquivalentTo([(Repo, Preferred)], CollectionOrdering.Matching); // legacy is not even requested
    }

    [Test] public async Task Legacy_file_still_works_when_the_preferred_file_is_missing()
    {
        var p = new FakeProvider(Files((Legacy, LegacyYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Legacy);
        await Assert.That(Enumerable.Single(r.Result.Config.Groups).Name).IsEqualTo("Legacy");
        await Assert.That(r.Result.Diagnostics).IsEmpty();
        await Assert.That(p.Requests.Select(x => x.Path)).IsEquivalentTo([Preferred, Legacy], CollectionOrdering.Matching);
    }

    [Test] public async Task Preferred_wins_over_legacy_when_both_exist()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, PreferredYaml), (Legacy, LegacyYaml))), Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(Enumerable.Single(r.Result.Config.Groups).Name).IsEqualTo("Preferred");
    }

    [Test] public async Task Missing_files_mean_no_configuration_and_no_diagnostics()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider([]), Repo, default);
        await Assert.That(r.SourcePath).IsNull();
        await Assert.That(r.Result.Config.Groups).IsEmpty();
        await Assert.That(r.Result.Diagnostics).IsEmpty();
    }

    [Test] public async Task Invalid_preferred_file_is_reported_and_never_falls_back_to_legacy()
    {
        var p = new FakeProvider(Files((Preferred, "groups:\n  - name: X\n    workflow: [a]\n"), (Legacy, LegacyYaml)));
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(r.Result.HasErrors).IsTrue();
        await Assert.That(r.Result.Config.Groups).DoesNotContain(g => g.Name == "Legacy");
        await Assert.That(p.Requests).DoesNotContain(x => x.Path == Legacy);
        await Assert.That(r.Result.Diagnostics.First(d => d.Line == 3).ToString(r.SourcePath!)).StartsWith(".github/ciworkbench.yml line 3:");
    }

    [Test] public async Task Syntax_error_in_preferred_file_ignores_it_entirely_without_using_legacy()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, "groups: [unclosed\n"), (Legacy, LegacyYaml))), Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(r.Result.Diagnostics).Contains(d => d.Code == "syntax-error");
        await Assert.That(r.Result.Config.Groups).IsEmpty();
    }

    [Test] public async Task Invalid_legacy_file_is_reported_against_the_legacy_path()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Legacy, "version: 99\n"))), Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Legacy);
        await Assert.That(r.Result.HasErrors).IsTrue();
        await Assert.That(r.Result.Diagnostics[0].ToString(r.SourcePath!)).StartsWith(".ciworkbench.yml line 1:");
    }

    [Test] public async Task Empty_preferred_file_counts_as_existing_and_hides_the_legacy_file()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, "# nothing yet\n"), (Legacy, LegacyYaml))), Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(r.Result.Config.Groups).IsEmpty();
    }

    [Test] public async Task Unreadable_preferred_file_warns_and_does_not_fall_back()
    {
        var p = new FakeProvider(Files((Legacy, LegacyYaml)), [(Repo, Preferred)]);
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Preferred);
        await Assert.That(r.Result.Config.Groups).IsEmpty();
        await Assert.That(r.Result.Diagnostics).Contains(d => d.Code == "unreadable" && d.Severity == ConfigSeverity.Warning);
        await Assert.That(r.Result.HasErrors).IsFalse();
    }

    [Test] public async Task Unreadable_legacy_file_warns_after_a_missing_preferred_file()
    {
        var r = await GroupConfigLoader.LoadAsync(new FakeProvider([], [(Repo, Legacy)]), Repo, default);
        await Assert.That(r.SourcePath).IsEqualTo(Legacy);
        await Assert.That(r.Result.Diagnostics).Contains(d => d.Code == "unreadable");
    }

    [Test] public async Task Only_the_selected_repository_is_queried_and_only_the_two_config_paths()
    {
        var other = new RepoRef("acme", "other");
        var files = Files((Preferred, PreferredYaml));
        files[(other, Preferred)] = "groups:\n  - name: Other\n";
        var p = new FakeProvider(files);
        var r = await GroupConfigLoader.LoadAsync(p, Repo, default);
        await Assert.That(r.Result.Config.Groups[0].Name).IsEqualTo("Preferred");
        foreach (var x in p.Requests)
        {
            await Assert.That(x.Repo).IsEqualTo(Repo);
            await Assert.That(GroupConfigContract.SearchPaths).Contains(x.Path);
        }
    }

    [Test] public async Task Same_contract_applies_to_both_locations()
    {
        var a = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Preferred, PreferredYaml))), Repo, default);
        var b = await GroupConfigLoader.LoadAsync(new FakeProvider(Files((Legacy, PreferredYaml))), Repo, default);
        await Assert.That(b.Result.Config.Groups.Select(g => (g.Name, string.Join(",", g.Workflows)))).IsEquivalentTo(a.Result.Config.Groups.Select(g => (g.Name, string.Join(",", g.Workflows))), CollectionOrdering.Matching);
        await Assert.That(b.Result.Diagnostics.Select(d => d.Message)).IsEquivalentTo(a.Result.Diagnostics.Select(d => d.Message), CollectionOrdering.Matching);
    }

    [Test] public async Task Diagnostics_keep_the_legacy_file_name_by_default_and_name_the_source_on_request()
    {
        var d = new ConfigDiagnostic(ConfigSeverity.Error, "x", "boom", 4);
        await Assert.That(d.ToString()).IsEqualTo(".ciworkbench.yml line 4: boom");
        await Assert.That(d.ToString(Preferred)).IsEqualTo(".github/ciworkbench.yml line 4: boom");
    }

    [Test] public async Task Published_schema_is_location_independent()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "ciworkbench.schema.json"));
        var root = JsonNode.Parse(text)!.AsObject();
        await Assert.That(root["title"]!.GetValue<string>()).Contains(".github/ciworkbench.yml");
        await Assert.That(root["$id"]!.GetValue<string>()).IsEqualTo(GroupConfigContract.SchemaUrl);
    }
}