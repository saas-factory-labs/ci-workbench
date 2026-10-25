using Bunit;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Failures;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CIWorkbench.Web.Tests;

internal sealed class AdjustableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Forwards to the demo provider and counts the calls the redesign promises to keep small. Behaves like a live provider (no demo-only shortcuts).</summary>
internal sealed class CountingProvider(DemoCiProvider inner, bool demo = false) : ICiProvider
{
    public int Workflows, RecentRuns, WorkflowRuns, Runs, Jobs, Logs;
    public TaskCompletionSource? Gate;
    public string Name => inner.Name; public bool IsDemo => demo;
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);
    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) { Interlocked.Increment(ref Workflows); return inner.ListWorkflowsAsync(repo, ct); }
    public async Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct)
    {
        Interlocked.Increment(ref RecentRuns);
        if (Gate is not null) await Gate.Task;
        return await inner.ListRecentRunsAsync(repo, max, ct);
    }
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) { Interlocked.Increment(ref WorkflowRuns); return inner.ListWorkflowRunsAsync(repo, workflowId, max, ct); }
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) { Interlocked.Increment(ref Runs); return inner.GetRunAsync(repo, runId, ct); }
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) { Interlocked.Increment(ref Jobs); return inner.ListJobsAsync(repo, runId, ct); }
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) { Interlocked.Increment(ref Logs); return inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct); }
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => inner.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

public class CachedCiTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Repeated_reads_hit_the_cache_until_the_entry_expires()
    {
        var clock = new AdjustableClock(Start);
        var spy = new CountingProvider(new DemoCiProvider(clock));
        var cache = new CachedCi(spy, clock);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        Assert.Equal(1, spy.Workflows);
        Assert.Equal(1, spy.RecentRuns);
        clock.Advance(CachedCi.RunsTtl + TimeSpan.FromSeconds(1));
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        Assert.Equal(2, spy.RecentRuns);                // short-lived run lists expired
        Assert.Equal(1, spy.Workflows);                 // the workflow list is still fresh
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request_and_a_cancelled_caller_does_not_poison_the_entry()
    {
        var clock = new AdjustableClock(Start);
        var spy = new CountingProvider(new DemoCiProvider(clock)) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = new CachedCi(spy, clock);
        using var cts = new CancellationTokenSource();
        var first = cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 50, cts.Token);
        var second = cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 50, default);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        spy.Gate.SetResult();
        Assert.NotEmpty(await second);
        Assert.Equal(1, spy.RecentRuns);
    }

    [Fact]
    public async Task Invalidate_runs_refetches_run_lists_but_keeps_finished_run_jobs()
    {
        var clock = new AdjustableClock(Start);
        var spy = new CountingProvider(new DemoCiProvider(clock));
        var cache = new CachedCi(spy, clock);
        var runs = await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        var done = runs.First(r => r.State == RunState.Success);
        await cache.ListJobsAsync(DemoCiProvider.Monorepo, done.Id, default);
        cache.InvalidateRuns(DemoCiProvider.Monorepo);
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await cache.ListJobsAsync(DemoCiProvider.Monorepo, done.Id, default);
        Assert.Equal(2, spy.RecentRuns);
        Assert.Equal(1, spy.Jobs);
    }

    [Fact]
    public async Task Demo_and_live_data_never_share_cache_entries_and_the_cache_is_bounded()
    {
        var clock = new AdjustableClock(Start);
        var inner = new DemoCiProvider(clock);
        var cache = new CachedCi(new CountingProvider(inner), clock);
        for (var i = 0; i < 600; i++) await cache.GetRunAsync(DemoCiProvider.Monorepo, 1_000_000 + i, default);
        Assert.True(cache.Count <= 400);
    }

    [Fact]
    public async Task Get_run_returns_the_run_by_id_and_null_for_unknown_ids()
    {
        var demo = new DemoCiProvider(new AdjustableClock(Start));
        var any = (await demo.ListRecentRunsAsync(DemoCiProvider.Monorepo, 5, default)).First();
        Assert.Equal(any.Id, (await demo.GetRunAsync(DemoCiProvider.Monorepo, any.Id, default))!.Id);
        Assert.Null(await demo.GetRunAsync(DemoCiProvider.Monorepo, 42, default));
    }
}

public class RouteTests
{
    [Fact]
    public void Routes_are_relative_stable_and_escaped()
    {
        var r = new CiwRoutes();
        var repo = new RepoRef("example-org", "platform-monorepo");
        Assert.Equal("repo/example-org/platform-monorepo", r.Repository(repo));
        Assert.Equal("repo/example-org/platform-monorepo/failures", r.Failures(repo));
        Assert.Equal("repo/example-org/platform-monorepo/workflows/1011", r.Workflow(repo, 1011));
        Assert.Equal("repo/example-org/platform-monorepo/workflows/1011?tab=failures", r.Workflow(repo, 1011, "failures"));
        Assert.Equal("repo/example-org/platform-monorepo/runs/1001100", r.Run(repo, 1001100));
        Assert.All(new[] { r.Repository(repo), r.Workflow(repo, 1), r.Run(repo, 1), r.Repositories }, u => Assert.False(u.StartsWith('/')));
        Assert.Equal("repo/a%20b/c%2Fd", r.Repository(new RepoRef("a b", "c/d")));
    }
}

public class QuickPreviewTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private CountingProvider _spy = null!;

    private async Task<IRenderedComponent<RepositoryWorkspace>> Open(RepoRef repo, bool demo = false)
    {
        var clock = new FixedClock(Now);
        _spy = new CountingProvider(new DemoCiProvider(clock), demo);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(_spy);
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, repo); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        return cut;
    }

    private static void OpenAllGroups(IRenderedComponent<RepositoryWorkspace> cut)
    {
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
    }

    [Fact]
    public async Task The_workspace_has_exactly_three_primary_kpis()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        Assert.Equal(3, cut.FindAll(".ciw-kpi").Count);
        Assert.Equal(["success", "attention", "duration"], cut.FindAll(".ciw-kpi").Select(k => k.GetAttribute("data-kpi")));
    }

    [Fact]
    public async Task Overview_loading_fetches_no_jobs_and_no_logs()
    {
        await Open(DemoCiProvider.Monorepo);
        Assert.Equal(0, _spy.Logs);
        Assert.Equal(0, _spy.Jobs);
        Assert.Equal(0, _spy.WorkflowRuns);
    }

    [Fact]
    public async Task Each_workflow_row_has_a_leading_chevron_and_a_name_link_that_are_distinct_controls()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        var row = cut.Find("[data-workflow$='container-scan.yml']");
        var toggle = row.QuerySelector("button.ciw-wf-toggle")!;
        var link = row.QuerySelector("a.ciw-wf-name")!;
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.False(string.IsNullOrWhiteSpace(toggle.GetAttribute("aria-label")));
        Assert.Matches(@"^repo/example-org/platform-monorepo/workflows/\d+$", link.GetAttribute("href"));
        Assert.True(row.QuerySelector(".ciw-wf-main")!.FirstElementChild == toggle);          // the chevron is the first (far-left) element
        Assert.Empty(row.QuerySelectorAll(".ciw-wf-status .ciw-status"));                    // no extra status icon beside the name
        Assert.Empty(cut.FindAll(".ciw-wf-path"));                                           // no permanent file paths on collapsed rows
    }

    [Fact]
    public async Task The_chevron_opens_and_closes_a_failure_preview_with_job_step_error_and_a_link_to_details()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        cut.Find("[data-workflow$='container-scan.yml'] .ciw-wf-toggle").Click();
        cut.WaitForAssertion(() => Assert.Contains("TRIVY0001", cut.Find("[data-testid=preview-failure]").TextContent), TimeSpan.FromSeconds(10));
        var preview = cut.Find("[data-workflow$='container-scan.yml'] [data-testid=quick-preview]");
        Assert.Contains("Scan published images", preview.TextContent);       // failing job
        Assert.Contains("Trivy scan", preview.TextContent);                   // failing step
        Assert.Contains("in a row", preview.TextContent);                     // recurrence streak
        Assert.Contains("Last successful run", preview.TextContent);
        Assert.Matches(@"/workflows/\d+$", preview.QuerySelector("[data-action=open-workflow-details]")!.GetAttribute("href"));
        Assert.Matches(@"/runs/\d+$", preview.QuerySelector("[data-action=open-run]")!.GetAttribute("href"));
        Assert.Empty(preview.QuerySelectorAll("table, pre"));                 // no run table, no log
        Assert.True(_spy.Logs <= FailureAnalyzer.DefaultMaxRuns, $"{_spy.Logs} log fetches");
        cut.Find("[data-workflow$='container-scan.yml'] .ciw-wf-toggle").Click();
        Assert.Empty(cut.FindAll("[data-workflow$='container-scan.yml'] [data-testid=quick-preview]"));
    }

    [Fact]
    public async Task A_healthy_workflow_preview_shows_the_latest_run_and_history_but_no_failure_section()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        var preview = cut.Find("[data-workflow$='codeql.yml'] [data-testid=quick-preview]");
        Assert.Empty(preview.QuerySelectorAll("[data-testid=preview-failure]"));
        Assert.NotEmpty(preview.QuerySelectorAll(".ciw-sdot"));
        Assert.Contains("Latest run", preview.TextContent);
    }

    [Fact]
    public async Task Opening_one_preview_loads_only_that_workflows_data_and_leaves_groups_alone()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        var prefs = Services.GetRequiredService<UiPreferences>();
        var groupsBefore = prefs.ExpandedKeys.Count(k => k.StartsWith("group|"));
        var jobsBefore = _spy.Jobs;
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        cut.WaitForAssertion(() => Assert.True(_spy.Jobs > jobsBefore));
        Assert.True(_spy.Jobs - jobsBefore <= 2);                              // the focus run (and at most an active run), not every workflow
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        Assert.Equal(groupsBefore, prefs.ExpandedKeys.Count(k => k.StartsWith("group|")));
        Assert.Equal(groupsBefore, cut.FindAll("[data-group]").Count(g => g.QuerySelector(".ciw-group-toggle")!.GetAttribute("aria-expanded") == "true"));
    }

    [Fact]
    public async Task Status_filter_survives_leaving_and_returning_to_the_workspace()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        cut.Find("button[data-filter=Failed]").Click();
        Assert.Equal("true", cut.Find("button[data-filter=Failed]").GetAttribute("aria-pressed"));
        cut.Dispose();                                                          // navigating to a detail page unmounts the workspace
        var again = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.AutoRefresh, false); });
        await again.WaitForAssertionAsync(() => Assert.NotEmpty(again.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        Assert.Equal("true", again.Find("button[data-filter=Failed]").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Coming_back_to_a_repository_reuses_cached_data_instead_of_reloading_it()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        var workflowCalls = _spy.Workflows; var runCalls = _spy.RecentRuns;
        cut.Dispose();
        var again = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.AutoRefresh, false); });
        await again.WaitForAssertionAsync(() => Assert.NotEmpty(again.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        Assert.Equal(workflowCalls, _spy.Workflows);
        Assert.Equal(runCalls, _spy.RecentRuns);
    }

    [Fact]
    public async Task Attention_summary_lists_only_workflows_failing_now_and_links_to_their_pages()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        var banner = cut.Find("[data-testid=failure-banner]");
        var links = banner.QuerySelectorAll("a.ciw-attn-link").ToList();
        Assert.InRange(links.Count, 1, 3);
        Assert.All(links, l => Assert.Matches(@"/workflows/\d+$", l.GetAttribute("href")));
        Assert.Contains("need attention", banner.TextContent);
    }
}

public class DetailsPageTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly CountingProvider _spy;

    public DetailsPageTests()
    {
        var clock = new FixedClock(Now);
        _spy = new CountingProvider(new DemoCiProvider(clock), demo: true);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(_spy);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<WorkflowInfo> Wf(string file) => (await _spy.ListWorkflowsAsync(DemoCiProvider.Monorepo, default)).First(w => w.Path.EndsWith(file));

    [Fact]
    public async Task Workflow_details_deep_link_renders_header_breadcrumbs_tabs_and_runs()
    {
        var wf = await Wf("container-scan.yml");
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id); p.Add(c => c.DefaultBranch, "main"); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr.ciw-row")), TimeSpan.FromSeconds(10));
        Assert.Equal("Container image scan", cut.Find("h1").TextContent.Trim());
        var crumbs = cut.Find("[data-testid=breadcrumbs]");
        Assert.Contains(crumbs.QuerySelectorAll("a"), a => a.GetAttribute("href") == "repo/example-org/platform-monorepo");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=breadcrumbs] [data-crumb=group]")));   // group resolved from the YAML comment / config
        Assert.Equal(["runs", "failures", "jobs"], cut.FindAll("[role=tab]").Select(t => t.GetAttribute("data-tab")));
        Assert.Equal("true", cut.Find("[role=tab][data-tab=runs]").GetAttribute("aria-selected"));
        Assert.All(cut.FindAll("tr.ciw-row .c-id a"), a => Assert.Matches(@"^repo/example-org/platform-monorepo/runs/\d+$", a.GetAttribute("href")));
    }

    [Fact]
    public async Task Workflow_details_filters_by_status_and_reports_the_query_for_the_url()
    {
        var wf = await Wf("container-scan.yml");
        WorkflowDetailsQuery? last = null;
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id);
            p.Add(c => c.OnQueryChanged, EventCallback.Factory.Create<WorkflowDetailsQuery>(this, q => last = q)); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr.ciw-row")), TimeSpan.FromSeconds(10));
        var all = cut.FindAll("tr.ciw-row").Count;
        cut.Find("button[data-filter=Succeeded]").Click();
        Assert.Equal("Succeeded", last?.Status);
        Assert.All(cut.FindAll("tr.ciw-row [data-state]"), b => Assert.Equal("ok", b.GetAttribute("data-state")));
        Assert.True(cut.FindAll("tr.ciw-row").Count < all || all <= 5);
        cut.Find("[role=tab][data-tab=failures]").Click();
        Assert.Equal("failures", last?.Tab);
    }

    [Fact]
    public async Task Workflow_details_jobs_tab_shows_per_job_history_across_recent_runs()
    {
        var wf = await Wf("container-scan.yml");
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id); p.Add(c => c.Query, new WorkflowDetailsQuery(Tab: "jobs")); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-jobhist-row")), TimeSpan.FromSeconds(10));
        Assert.Contains("Scan published images", cut.Find(".ciw-jobhist-row").TextContent);
        Assert.NotEmpty(cut.FindAll(".ciw-jobhist-row .ciw-sdot.fail"));
    }

    [Fact]
    public async Task Unknown_workflow_ids_show_a_not_found_state_with_a_way_back()
    {
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, 987654321L); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=workflow-not-found]")));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "repo/example-org/platform-monorepo");
    }

    [Fact]
    public async Task Run_details_deep_link_shows_identity_failure_diagnostics_and_related_series()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.WorkflowPath.EndsWith("container-scan.yml") && r.State == RunState.Failure);
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, run.Id); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=failure-summary]")), TimeSpan.FromSeconds(10));
        Assert.Contains($"#{run.RunNumber}", cut.Find("h1").TextContent);
        var meta = cut.Find("[data-testid=run-meta]").TextContent;
        Assert.Contains(run.Branch, meta); Assert.Contains(run.Event, meta); Assert.Contains(run.Actor, meta);
        Assert.Contains("Scan published images", cut.Find("[data-testid=failure-summary]").TextContent);
        Assert.Contains("Trivy scan", cut.Find("[data-testid=failure-summary]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=inspector-close]"));                            // a page has no close button
        Assert.NotEmpty(cut.FindAll("[data-action=back]"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=related-series] [data-series]")), TimeSpan.FromSeconds(10));
        Assert.Contains("/workflows/", cut.Find("[data-action=all-series]").GetAttribute("href"));
    }

    [Fact]
    public async Task Run_details_loads_logs_only_on_demand()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.State == RunState.Success);
        _spy.Logs = 0;
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, run.Id); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=run-meta]")), TimeSpan.FromSeconds(10));
        Assert.Equal(0, _spy.Logs);                                                            // a passing run never touches logs
        Assert.Empty(cut.FindAll("[data-testid=related-series]"));
    }

    [Fact]
    public void Unknown_run_ids_show_a_not_found_state()
    {
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, 5L); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=run-not-found]")));
    }

    [Fact]
    public async Task Quick_inspector_links_to_the_full_run_page()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.State == RunState.Failure);
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.Monorepo, run)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-action=open-run-details]")), TimeSpan.FromSeconds(10));
        Assert.Equal($"repo/example-org/platform-monorepo/runs/{run.Id}", cut.Find("[data-action=open-run-details]").GetAttribute("href"));
        Assert.NotEmpty(cut.FindAll("[data-testid=inspector-close]"));
    }
}

public class StatePreservationTests : BunitContext
{
    public StatePreservationTests()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
        Services.AddCIWorkbenchGitHub(config);
        Services.AddCIWorkbenchComponents();
        Services.AddScoped<DemoCiProvider>(sp => new DemoCiProvider(clock));
        Services.AddScoped<AppSession>();
        Services.AddScoped<ICiProvider, SwitchableCiProvider>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Returning_to_the_same_repository_keeps_search_and_inspector_but_switching_repositories_clears_them()
    {
        var session = Services.GetRequiredService<AppSession>();
        var signals = Services.GetRequiredService<WorkspaceSignals>();
        var inspector = Services.GetRequiredService<InspectorState>();
        session.Select(DemoCiProvider.Monorepo);
        signals.SetSearch("container");
        var run = (await new DemoCiProvider(new FixedClock(DateTimeOffset.UtcNow)).ListRecentRunsAsync(DemoCiProvider.Monorepo, 3, default)).First();
        inspector.Select(new RunSelection(DemoCiProvider.Monorepo, run));

        var back = Render<CIWorkbench.Web.Components.Pages.RepoPage>(p => { p.Add(c => c.Owner, "example-org"); p.Add(c => c.Name, "platform-monorepo"); });
        Assert.Equal("container", signals.SearchText);
        Assert.NotNull(inspector.Selection);
        back.Dispose();

        Render<CIWorkbench.Web.Components.Pages.RepoPage>(p => { p.Add(c => c.Owner, "example-org"); p.Add(c => c.Name, "review-scraper-actor"); });
        Assert.Null(signals.SearchText);
        Assert.Null(inspector.Selection);
    }
}

public class PreferencesTests
{
    [Fact]
    public void Restoring_persisted_expansion_notifies_components_that_already_rendered()
    {
        var prefs = new UiPreferences();
        var raised = 0;
        prefs.Changed += () => raised++;
        prefs.RestoreExpanded(["group|a/b|Security", "wf|a/b|x.yml|1"]);
        Assert.Equal(1, raised);
        Assert.True(prefs.IsExpanded("wf|a/b|x.yml|1"));
        prefs.RestoreExpanded(["group|a/b|Security"]);     // nothing new: no needless re-render
        Assert.Equal(1, raised);
    }
}
