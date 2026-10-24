using Bunit;
using Microsoft.AspNetCore.Components;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using CIWorkbench.GitHub;
using CIWorkbench.Web;

namespace CIWorkbench.Web.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

public class ComponentTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public ComponentTests()
    {
        var clock = new FixedClock(Now);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(new DemoCiProvider(clock));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<IRenderedComponent<RepositoryWorkspace>> RenderWorkspace(RepoRef repo, Action<ComponentParameterCollectionBuilder<RepositoryWorkspace>>? more = null)
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, repo); p.Add(c => c.AutoRefresh, false); more?.Invoke(p); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        return cut;
    }

    [Fact]
    public async Task Monorepo_renders_groups_collapsed_and_workflows_only_when_expanded()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var all = cut.FindAll("[data-group]");
        Assert.True(all.Count >= 5);
        var before = cut.FindAll("[data-workflow]").Count;
        cut.Find("[data-group] .ciw-group-head").Click();
        Assert.True(cut.FindAll("[data-workflow]").Count > before);
    }

    [Fact]
    public async Task Expand_state_is_preserved_across_rerender()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var prefs = Services.GetRequiredService<UiPreferences>();
        cut.Find("[data-group] .ciw-group-head").Click();
        var open = prefs.ExpandedKeys.Count(k => k.StartsWith("group|"));
        cut.Render();
        Assert.Equal(open, prefs.ExpandedKeys.Count(k => k.StartsWith("group|")));
        Assert.Equal("true", cut.Find("[data-group] .ciw-group-head").GetAttribute("aria-expanded"));
        cut.Find("[data-group] .ciw-group-head").Click();
        Assert.Equal("false", cut.Find("[data-group] .ciw-group-head").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task Search_filters_workflows_and_status_chip_filters_by_latest_state()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        cut.Find("button[data-filter=Failed]").Click();
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
        var failed = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        Assert.NotEmpty(failed);
        Assert.Contains(".github/workflows/container-scan.yml", failed);
        Services.GetRequiredService<WorkspaceSignals>().SetSearch("container-scan");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-workflow]")));
    }

    [Fact]
    public async Task Actor_repo_shows_banner_two_plus_series_and_selecting_a_run_raises_callback()
    {
        RunSelection? picked = null;
        var cut = await RenderWorkspace(DemoCiProvider.ActorRepo, p => p.Add(c => c.OnRunSelected, EventCallback.Factory.Create<RunSelection>(this, s => picked = s)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=failure-banner]")));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-series]").Count));
        Assert.Single(cut.FindAll("[data-oneoff]"));
        cut.Find("[data-series]").Click();
        Assert.NotEmpty(cut.FindAll(".ciw-series-body"));
        cut.Find("tr.ciw-row").Click();
        Assert.NotNull(picked);
        Assert.Equal(DemoCiProvider.ActorRepo, picked!.Repo);
    }

    [Fact]
    public async Task Inspector_shows_failed_jobs_first_and_loads_logs_only_on_demand()
    {
        var provider = Services.GetRequiredService<ICiProvider>();
        var run = (await provider.ListRecentRunsAsync(DemoCiProvider.ActorRepo, 10, default)).First(r => r.State == RunState.Failure);
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run, "scenario-tests")));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-jobitem")));
        var first = cut.FindAll(".ciw-jobitem")[0];
        Assert.Contains("fail", first.ClassList);
        Assert.Empty(cut.FindAll(".ciw-log"));                       // no log fetched yet
        cut.FindAll("button").First(b => b.TextContent.Contains("Load log excerpt")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-log")));
        Assert.Contains("Copy context for coding agent", cut.Markup);
    }

    [Fact]
    public void Inspector_without_selection_shows_placeholder() =>
        Assert.Contains("Select a run", Render<RunInspector>().Markup);

    [Fact]
    public void Density_toggle_switches_preferences_without_reload()
    {
        Assert.Equal("true", Render<DensityToggle>().FindAll("button")[0].GetAttribute("aria-pressed"));
        var prefs = Services.GetRequiredService<UiPreferences>();
        var cut = Render<DensityToggle>();
        cut.FindAll("button").First(b => b.TextContent == "Compact").Click();
        Assert.Equal(Density.Compact, prefs.Density);
        Assert.Contains("on", cut.FindAll("button").First(b => b.TextContent == "Compact").ClassList);
    }

    [Fact]
    public async Task Failures_view_lists_series_per_workflow()
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.View, WorkspaceView.Failures); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.Equal(2, cut.FindAll("[data-series]").Count), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Components_do_not_depend_on_host_layout_or_routes()
    {
        var asm = typeof(RepositoryWorkspace).Assembly;
        Assert.DoesNotContain(asm.GetTypes(), t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), true).Length > 0);
        Assert.DoesNotContain(asm.GetReferencedAssemblies(), a => a.Name!.Contains("SaaS", StringComparison.OrdinalIgnoreCase) || a.Name!.Contains("AppBlueprint", StringComparison.OrdinalIgnoreCase));
    }
}

public class NavigationTests : BunitContext
{
    private void Arrange()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
        Services.AddCIWorkbenchGitHub(config);
        Services.AddScoped<DemoCiProvider>(sp => new DemoCiProvider(clock));
        Services.AddScoped<AppSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("/overview", "Overview")]
    [InlineData("/repositories", "Repositories")]
    [InlineData("/repo/example-org/platform-monorepo", "Workflow Groups")]
    [InlineData("/repo/example-org/platform-monorepo/failures", "Failure Series")]
    [InlineData("/settings", "Settings")]
    public void Exactly_one_primary_nav_item_is_active_for_each_route(string path, string expected)
    {
        Arrange();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(path);
        var cut = Render<CIWorkbench.Web.Components.Layout.SideNav>();
        var active = cut.FindAll("a.nav.active");
        Assert.Single(active);
        Assert.Contains(expected, active[0].TextContent);
        Assert.Equal("page", active[0].GetAttribute("aria-current"));
    }
}
internal sealed class SpyProvider(DemoCiProvider inner) : ICiProvider
{
    public int WorkflowRunCalls;
    public string Name => inner.Name; public bool IsDemo => false;   // behave like a live provider (no demo-only shortcuts)
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);
    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) => inner.ListWorkflowsAsync(repo, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) => inner.ListRecentRunsAsync(repo, max, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) { Interlocked.Increment(ref WorkflowRunCalls); return inner.ListWorkflowRunsAsync(repo, workflowId, max, ct); }
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => inner.ListJobsAsync(repo, runId, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct);
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => inner.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

public class HistoryWindowTests : BunitContext
{
    [Fact]
    public async Task Selecting_a_workflow_in_a_truncated_window_fetches_one_bounded_page_of_its_own_history()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var spy = new SpyProvider(new DemoCiProvider(clock));
        Services.AddSingleton<TimeProvider>(clock); Services.AddCIWorkbenchComponents(); Services.AddSingleton<ICiProvider>(spy);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // MaxRuns=60 truncates the shared window (the fixture has ~246 runs), so most workflows hold only 0-2 runs in it.
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.MaxRuns, 60); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        await Task.Delay(400);                                          // let the first-view "open the best workflow" step finish
        Assert.True(spy.WorkflowRunCalls <= 1);                         // overview itself never fetches per-workflow history; only the one opened workflow may
        var before = spy.WorkflowRunCalls;
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
        // Open three workflows (the first-view "best" one may already have its history cached).
        for (var i = 0; i < 3; i++) { cut.FindAll("[data-workflow] .ciw-wf-head")[i].Click(); await Task.Delay(150); }
        Assert.InRange(spy.WorkflowRunCalls - before, 1, 3);                 // at most one bounded request per opened workflow, and the cache is used
    }
}
internal sealed class ConfigOverrideProvider(ICiProvider inner, string configYaml) : ICiProvider
{
    public string Name => inner.Name; public bool IsDemo => inner.IsDemo;
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);
    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) => inner.ListWorkflowsAsync(repo, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) => inner.ListRecentRunsAsync(repo, max, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) => inner.ListWorkflowRunsAsync(repo, workflowId, max, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => inner.ListJobsAsync(repo, runId, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct);
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => path == ".ciworkbench.yml" ? Task.FromResult<string?>(configYaml) : inner.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

public class ConfigDiagnosticsUiTests : BunitContext
{
    [Fact]
    public async Task Invalid_config_is_explained_with_line_numbers_and_never_hides_a_workflow()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        // line 3 has a typo ('workflow'), and a second group points at a file that does not exist
        const string yaml = "groups:\n  - name: CI\n    workflow: [.github/workflows/ci.yml]\n  - name: Quality\n    workflows:\n      - .github/workflows/quality.yml\n      - .github/workflows/missing.yml\n";
        Services.AddSingleton<TimeProvider>(clock); Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(new ConfigOverrideProvider(new DemoCiProvider(clock), yaml));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));

        var notices = cut.WaitForElements(".ciw-notice.warn", TimeSpan.FromSeconds(5)).Select(e => e.TextContent).ToList();
        Assert.Contains(notices, n => n.Contains(".ciworkbench.yml line 3") && n.Contains("did you mean 'workflows'"));
        Assert.Contains(notices, n => n.Contains("was skipped") && n.Contains("'CI'"));
        Assert.Contains(notices, n => n.Contains("line 7") && n.Contains("missing.yml") && n.Contains("matches no workflow"));

        // the skipped group's workflow is still visible (small repositories open fully); the valid group still applies
        var paths = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        Assert.Contains(".github/workflows/ci.yml", paths);
        Assert.Contains(".github/workflows/quality.yml", paths);
    }
}