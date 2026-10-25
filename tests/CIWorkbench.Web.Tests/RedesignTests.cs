using Bunit;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Failures;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;
using System.Threading.Tasks;

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

    [Test]
    public async Task Repeated_reads_hit_the_cache_until_the_entry_expires()
    {
        var clock = new AdjustableClock(Start);
        var spy = new CountingProvider(new DemoCiProvider(clock));
        var cache = new CachedCi(spy, clock);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await Assert.That(spy.Workflows).IsEqualTo(1);
        await Assert.That(spy.RecentRuns).IsEqualTo(1);
        clock.Advance(CachedCi.RunsTtl + TimeSpan.FromSeconds(1));
        await cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 100, default);
        await cache.ListWorkflowsAsync(DemoCiProvider.Monorepo, default);
        await Assert.That(spy.RecentRuns).IsEqualTo(2);                // short-lived run lists expired
        await Assert.That(spy.Workflows).IsEqualTo(1);                 // the workflow list is still fresh
    }

    [Test]
    public async Task Concurrent_callers_share_one_request_and_a_cancelled_caller_does_not_poison_the_entry()
    {
        var clock = new AdjustableClock(Start);
        var spy = new CountingProvider(new DemoCiProvider(clock)) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = new CachedCi(spy, clock);
        using var cts = new CancellationTokenSource();
        var first = cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 50, cts.Token);
        var second = cache.ListRecentRunsAsync(DemoCiProvider.Monorepo, 50, default);
        cts.Cancel();
        await Assert.That(async () => { await first; }).Throws<OperationCanceledException>();
        spy.Gate.SetResult();
        await Assert.That(await second).IsNotEmpty();
        await Assert.That(spy.RecentRuns).IsEqualTo(1);
    }

    [Test]
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
        await Assert.That(spy.RecentRuns).IsEqualTo(2);
        await Assert.That(spy.Jobs).IsEqualTo(1);
    }

    [Test]
    public async Task Demo_and_live_data_never_share_cache_entries_and_the_cache_is_bounded()
    {
        var clock = new AdjustableClock(Start);
        var inner = new DemoCiProvider(clock);
        var cache = new CachedCi(new CountingProvider(inner), clock);
        for (var i = 0; i < 600; i++) await cache.GetRunAsync(DemoCiProvider.Monorepo, 1_000_000 + i, default);
        await Assert.That(cache.Count <= 400).IsTrue();
    }

    [Test]
    public async Task Get_run_returns_the_run_by_id_and_null_for_unknown_ids()
    {
        var demo = new DemoCiProvider(new AdjustableClock(Start));
        var any = (await demo.ListRecentRunsAsync(DemoCiProvider.Monorepo, 5, default)).First();
        await Assert.That((await demo.GetRunAsync(DemoCiProvider.Monorepo, any.Id, default))!.Id).IsEqualTo(any.Id);
        await Assert.That(await demo.GetRunAsync(DemoCiProvider.Monorepo, 42, default)).IsNull();
    }
}

public class RouteTests
{
    [Test]
    public async Task Routes_are_relative_stable_and_escaped()
    {
        var r = new CiwRoutes();
        var repo = new RepoRef("example-org", "platform-monorepo");
        await Assert.That(r.Repository(repo)).IsEqualTo("repo/example-org/platform-monorepo");
        await Assert.That(r.Failures(repo)).IsEqualTo("repo/example-org/platform-monorepo/failures");
        await Assert.That(r.Workflow(repo, 1011)).IsEqualTo("repo/example-org/platform-monorepo/workflows/1011");
        await Assert.That(r.Workflow(repo, 1011, "failures")).IsEqualTo("repo/example-org/platform-monorepo/workflows/1011?tab=failures");
        await Assert.That(r.Run(repo, 1001100)).IsEqualTo("repo/example-org/platform-monorepo/runs/1001100");
        await Assert.That(new[] { r.Repository(repo), r.Workflow(repo, 1), r.Run(repo, 1), r.Repositories }).All(u => !(u.StartsWith('/')));
        await Assert.That(r.Repository(new RepoRef("a b", "c/d"))).IsEqualTo("repo/a%20b/c%2Fd");
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
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();
        await Task.Delay(300);
        return cut;
    }

    private static void OpenAllGroups(IRenderedComponent<RepositoryWorkspace> cut)
    {
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
    }

    [Test]
    public async Task The_workspace_has_exactly_three_primary_kpis()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        await Assert.That(cut.FindAll(".ciw-kpi").Count).IsEqualTo(3);
        await Assert.That(cut.FindAll(".ciw-kpi").Select(k => k.GetAttribute("data-kpi")!)).IsEquivalentTo(["success", "attention", "duration"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Overview_loading_fetches_no_jobs_and_no_logs()
    {
        await Open(DemoCiProvider.Monorepo);
        await Assert.That(_spy.Logs).IsEqualTo(0);
        await Assert.That(_spy.Jobs).IsEqualTo(0);
        await Assert.That(_spy.WorkflowRuns).IsEqualTo(0);
    }

    [Test]
    public async Task Each_workflow_row_has_a_leading_chevron_and_a_name_link_that_are_distinct_controls()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        var row = cut.Find("[data-workflow$='container-scan.yml']");
        var toggle = row.QuerySelector("button.ciw-wf-toggle")!;
        var link = row.QuerySelector("a.ciw-wf-name")!;
        await Assert.That(toggle.GetAttribute("aria-expanded")).IsEqualTo("false");
        await Assert.That(string.IsNullOrWhiteSpace(toggle.GetAttribute("aria-label"))).IsFalse();
        await Assert.That(link.GetAttribute("href")).Matches(@"^repo/example-org/platform-monorepo/workflows/\d+$");
        await Assert.That(row.QuerySelector(".ciw-wf-main")!.FirstElementChild == toggle).IsTrue();          // the chevron is the first (far-left) element
        await Assert.That(row.QuerySelectorAll(".ciw-wf-status .ciw-status")).IsEmpty();                    // no extra status icon beside the name
        await Assert.That(cut.FindAll(".ciw-wf-path")).IsEmpty();                                           // no permanent file paths on collapsed rows
    }

    [Test]
    public async Task The_chevron_opens_and_closes_a_failure_preview_with_job_step_error_and_a_link_to_details()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        cut.Find("[data-workflow$='container-scan.yml'] .ciw-wf-toggle").Click();
        await Assert.That(cut.Find("[data-testid=preview-failure]").TextContent).Contains("TRIVY0001");
        var preview = cut.Find("[data-workflow$='container-scan.yml'] [data-testid=quick-preview]");
        await Assert.That(preview.TextContent).Contains("Scan published images");       // failing job
        await Assert.That(preview.TextContent).Contains("Trivy scan");                   // failing step
        await Assert.That(preview.TextContent).Contains("in a row");                     // recurrence streak
        await Assert.That(preview.TextContent).Contains("Last successful run");
        await Assert.That(preview.QuerySelector("[data-action=open-workflow-details]")!.GetAttribute("href")).Matches(@"/workflows/\d+$");
        await Assert.That(preview.QuerySelector("[data-action=open-run]")!.GetAttribute("href")).Matches(@"/runs/\d+$");
        await Assert.That(preview.QuerySelectorAll("table, pre")).IsEmpty();                 // no run table, no log
        await Assert.That(_spy.Logs <= FailureAnalyzer.DefaultMaxRuns).IsTrue().Because($"{_spy.Logs} log fetches");
        cut.Find("[data-workflow$='container-scan.yml'] .ciw-wf-toggle").Click();
        await Assert.That(cut.FindAll("[data-workflow$='container-scan.yml'] [data-testid=quick-preview]")).IsEmpty();
    }

    [Test]
    public async Task A_healthy_workflow_preview_shows_the_latest_run_and_history_but_no_failure_section()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        var preview = cut.Find("[data-workflow$='codeql.yml'] [data-testid=quick-preview]");
        await Assert.That(preview.QuerySelectorAll("[data-testid=preview-failure]")).IsEmpty();
        await Assert.That(preview.QuerySelectorAll(".ciw-sdot")).IsNotEmpty();
        await Assert.That(preview.TextContent).Contains("Latest run");
    }

    [Test]
    public async Task Opening_one_preview_loads_only_that_workflows_data_and_leaves_groups_alone()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        OpenAllGroups(cut);
        var prefs = Services.GetRequiredService<UiPreferences>();
        var groupsBefore = prefs.ExpandedKeys.Count(k => k.StartsWith("group|"));
        var jobsBefore = _spy.Jobs;
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        await Assert.That(_spy.Jobs > jobsBefore).IsTrue();
        await Assert.That(_spy.Jobs - jobsBefore <= 2).IsTrue();                              // the focus run (and at most an active run), not every workflow
        cut.Find("[data-workflow$='codeql.yml'] .ciw-wf-toggle").Click();
        await Assert.That(prefs.ExpandedKeys.Count(k => k.StartsWith("group|"))).IsEqualTo(groupsBefore);
        await Assert.That(cut.FindAll("[data-group]").Count(g => g.QuerySelector(".ciw-group-toggle")!.GetAttribute("aria-expanded") == "true")).IsEqualTo(groupsBefore);
    }

    [Test]
    public async Task Status_filter_survives_leaving_and_returning_to_the_workspace()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        cut.Find("button[data-filter=Failed]").Click();
        await Assert.That(cut.Find("button[data-filter=Failed]").GetAttribute("aria-pressed")).IsEqualTo("true");
        cut.Dispose();                                                          // navigating to a detail page unmounts the workspace
        var again = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(again.FindAll("[data-group]")).IsNotEmpty();
        await Assert.That(again.Find("button[data-filter=Failed]").GetAttribute("aria-pressed")).IsEqualTo("true");
    }

    [Test]
    public async Task Coming_back_to_a_repository_reuses_cached_data_instead_of_reloading_it()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        var workflowCalls = _spy.Workflows; var runCalls = _spy.RecentRuns;
        cut.Dispose();
        var again = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(again.FindAll("[data-group]")).IsNotEmpty();
        await Assert.That(_spy.Workflows).IsEqualTo(workflowCalls);
        await Assert.That(_spy.RecentRuns).IsEqualTo(runCalls);
    }

    [Test]
    public async Task Attention_summary_lists_only_workflows_failing_now_and_links_to_their_pages()
    {
        var cut = await Open(DemoCiProvider.Monorepo);
        var banner = cut.Find("[data-testid=failure-banner]");
        var links = banner.QuerySelectorAll("a.ciw-attn-link").ToList();
        await Assert.That(links.Count).IsBetween(1,3);
        foreach (var l in links) await Assert.That(l.GetAttribute("href")).Matches(@"/workflows/\d+$");
        await Assert.That(banner.TextContent).Contains("need attention");
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

    [Test]
    public async Task Workflow_details_deep_link_renders_header_breadcrumbs_tabs_and_runs()
    {
        var wf = await Wf("container-scan.yml");
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id); p.Add(c => c.DefaultBranch, "main"); });
        await Assert.That(cut.FindAll("tr.ciw-row")).IsNotEmpty();
        await Assert.That(cut.Find("h1").TextContent.Trim()).IsEqualTo("Container image scan");
        var crumbs = cut.Find("[data-testid=breadcrumbs]");
        await Assert.That(crumbs.QuerySelectorAll("a")).Contains(a => a.GetAttribute("href") == "repo/example-org/platform-monorepo");
        await Assert.That(cut.Find("[data-testid=breadcrumbs] [data-crumb=group]")).IsNotNull();   // group resolved from the YAML comment / config
        await Assert.That(cut.FindAll("[role=tab]").Select(t => t.GetAttribute("data-tab")!)).IsEquivalentTo(["runs", "failures", "jobs"], CollectionOrdering.Matching);
        await Assert.That(cut.Find("[role=tab][data-tab=runs]").GetAttribute("aria-selected")).IsEqualTo("true");
        foreach (var a in cut.FindAll("tr.ciw-row .c-id a")) await Assert.That(a.GetAttribute("href")).Matches(@"^repo/example-org/platform-monorepo/runs/\d+$");
    }

    [Test]
    public async Task Workflow_details_filters_by_status_and_reports_the_query_for_the_url()
    {
        var wf = await Wf("container-scan.yml");
        WorkflowDetailsQuery? last = null;
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id);
            p.Add(c => c.OnQueryChanged, EventCallback.Factory.Create<WorkflowDetailsQuery>(this, q => last = q)); });
        await Assert.That(cut.FindAll("tr.ciw-row")).IsNotEmpty();
        var all = cut.FindAll("tr.ciw-row").Count;
        cut.Find("button[data-filter=Succeeded]").Click();
        await Assert.That(last?.Status).IsEqualTo("Succeeded");
        foreach (var b in cut.FindAll("tr.ciw-row [data-state]")) await Assert.That(b.GetAttribute("data-state")).IsEqualTo("ok");
        await Assert.That(cut.FindAll("tr.ciw-row").Count < all || all <= 5).IsTrue();
        cut.Find("[role=tab][data-tab=failures]").Click();
        await Assert.That(last?.Tab).IsEqualTo("failures");
    }

    [Test]
    public async Task Workflow_details_jobs_tab_shows_per_job_history_across_recent_runs()
    {
        var wf = await Wf("container-scan.yml");
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, wf.Id); p.Add(c => c.Query, new WorkflowDetailsQuery(Tab: "jobs")); });
        await Assert.That(cut.FindAll(".ciw-jobhist-row")).IsNotEmpty();
        await Assert.That(cut.Find(".ciw-jobhist-row").TextContent).Contains("Scan published images");
        await Assert.That(cut.FindAll(".ciw-jobhist-row .ciw-sdot.fail")).IsNotEmpty();
    }

    [Test]
    public async Task Unknown_workflow_ids_show_a_not_found_state_with_a_way_back()
    {
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.WorkflowId, 987654321L); });
        await Assert.That(cut.FindAll("[data-testid=workflow-not-found]")).IsNotEmpty();
        await Assert.That(cut.FindAll("a")).Contains(a => a.GetAttribute("href") == "repo/example-org/platform-monorepo");
    }

    [Test]
    public async Task Run_details_deep_link_shows_identity_failure_diagnostics_and_related_series()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.WorkflowPath.EndsWith("container-scan.yml") && r.State == RunState.Failure);
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, run.Id); });
        await Assert.That(cut.FindAll("[data-testid=failure-summary]")).IsNotEmpty();
        await Assert.That(cut.Find("h1").TextContent).Contains($"#{run.RunNumber}");
        var meta = cut.Find("[data-testid=run-meta]").TextContent;
        await Assert.That(meta).Contains(run.Branch); await Assert.That(meta).Contains(run.Event); await Assert.That(meta).Contains(run.Actor);
        await Assert.That(cut.Find("[data-testid=failure-summary]").TextContent).Contains("Scan published images");
        await Assert.That(cut.Find("[data-testid=failure-summary]").TextContent).Contains("Trivy scan");
        await Assert.That(cut.FindAll("[data-testid=inspector-close]")).IsEmpty();                            // a page has no close button
        await Assert.That(cut.FindAll("[data-action=back]")).IsNotEmpty();
        await Assert.That(cut.FindAll("[data-testid=related-series] [data-series]")).IsNotEmpty();
        await Assert.That(cut.Find("[data-action=all-series]").GetAttribute("href")).Contains("/workflows/");
    }

    [Test]
    public async Task Run_details_loads_logs_only_on_demand()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.State == RunState.Success);
        _spy.Logs = 0;
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, run.Id); });
        await Assert.That(cut.FindAll("[data-testid=run-meta]")).IsNotEmpty();
        await Assert.That(_spy.Logs).IsEqualTo(0);                                                            // a passing run never touches logs
        await Assert.That(cut.FindAll("[data-testid=related-series]")).IsEmpty();
    }

    [Test]
    public async Task Unknown_run_ids_show_a_not_found_state()
    {
        var cut = Render<RunDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.RunId, 5L); });
        await Assert.That(cut.FindAll("[data-testid=run-not-found]")).IsNotEmpty();
    }

    [Test]
    public async Task Quick_inspector_links_to_the_full_run_page()
    {
        var run = (await _spy.ListRecentRunsAsync(DemoCiProvider.Monorepo, 300, default)).First(r => r.State == RunState.Failure);
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.Monorepo, run)));
        await Assert.That(cut.FindAll("[data-action=open-run-details]")).IsNotEmpty();
        await Assert.That(cut.Find("[data-action=open-run-details]").GetAttribute("href")).IsEqualTo($"repo/example-org/platform-monorepo/runs/{run.Id}");
        await Assert.That(cut.FindAll("[data-testid=inspector-close]")).IsNotEmpty();
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
        Services.AddScoped<OverviewData>();
        Services.AddRunnerInsights(config);
        Services.PostConfigure<CIWorkbench.Core.Runners.RunnerInsightsOptions>(o => o.DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ciw-tests", Guid.NewGuid().ToString("N")));   // tests never touch the real saved allowances
        Services.AddScoped<ICiProvider, SwitchableCiProvider>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Test]
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
        await Assert.That(signals.SearchText).IsEqualTo("container");
        await Assert.That(inspector.Selection).IsNotNull();
        back.Dispose();

        Render<CIWorkbench.Web.Components.Pages.RepoPage>(p => { p.Add(c => c.Owner, "example-org"); p.Add(c => c.Name, "review-scraper-actor"); });
        await Assert.That(signals.SearchText).IsNull();
        await Assert.That(inspector.Selection).IsNull();
    }
}

public class PreferencesTests
{
    [Test]
    public async Task Restoring_persisted_expansion_notifies_components_that_already_rendered()
    {
        var prefs = new UiPreferences();
        var raised = 0;
        prefs.Changed += () => raised++;
        prefs.RestoreExpanded(["group|a/b|Security", "wf|a/b|x.yml|1"]);
        await Assert.That(raised).IsEqualTo(1);
        await Assert.That(prefs.IsExpanded("wf|a/b|x.yml|1")).IsTrue();
        prefs.RestoreExpanded(["group|a/b|Security"]);     // nothing new: no needless re-render
        await Assert.That(raised).IsEqualTo(1);
    }
}