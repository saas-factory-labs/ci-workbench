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
        Assert.Equal("true", cut.Find("[data-group] .ciw-group-toggle").GetAttribute("aria-expanded"));
        Assert.All(prefs.ExpandedKeys.Where(k => k.StartsWith("group|")), k => Assert.StartsWith("group|example-org/platform-monorepo|", k));   // state is per repository
        cut.Find("[data-group] .ciw-group-head").Click();
        Assert.Equal("false", cut.Find("[data-group] .ciw-group-toggle").GetAttribute("aria-expanded"));
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

    private async Task<WorkflowRunInfo> FailedActorRun()
    {
        var provider = Services.GetRequiredService<ICiProvider>();
        return (await provider.ListRecentRunsAsync(DemoCiProvider.ActorRepo, 10, default)).First(r => r.State == RunState.Failure);
    }

    [Fact]
    public async Task Inspector_summary_is_failure_first_and_labels_the_detected_error_as_heuristic()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=failure-summary]")), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=cause]")), TimeSpan.FromSeconds(5));
        Assert.Contains("Detected error", cut.Find("[data-testid=cause]").TextContent);
        Assert.Contains("not interpreted", cut.Find("[data-testid=cause]").TextContent);
        Assert.NotEmpty(cut.FindAll(".ciw-log .ciw-logline.err"));                    // error line is highlighted
        Assert.DoesNotContain("##[error]", cut.Find(".ciw-log").TextContent);         // decorations stripped after the error was identified
        Assert.Contains("Copy context for coding agent", cut.Markup);
        Assert.DoesNotContain("AI", cut.Find("[data-testid=cause]").TextContent.Replace("detected", ""));   // never claims AI analysis
    }

    [Fact]
    public async Task Inspector_jobs_tab_lists_failed_jobs_first_and_keeps_successful_jobs()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-tab=Jobs]")));
        cut.Find("[data-tab=Jobs]").Click();
        var jobs = cut.FindAll(".ciw-jobitem");
        Assert.Contains("fail", jobs[0].ClassList);
        Assert.Contains(jobs, j => j.ClassList.Contains("ok"));                         // passed jobs are preserved
        Assert.Equal("true", cut.Find("[data-tab=Jobs]").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task Inspector_logs_tab_is_bounded_and_has_a_view_full_logs_action_disabled_in_demo()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=cause]")), TimeSpan.FromSeconds(5));
        Assert.Contains("View full logs", cut.Markup);
        Assert.Contains("disabled", cut.Find(".ciw-insp-actions .ciw-btn.disabled").ClassList);
        cut.Find("[data-tab=Logs]").Click();
        Assert.True(cut.FindAll(".ciw-log .ciw-logline").Count <= 120);
    }

    [Fact]
    public async Task Inspector_header_close_and_similar_failures_raise_host_callbacks()
    {
        var run = await FailedActorRun();
        var closed = 0; RunSelection? similar = null;
        var cut = Render<RunInspector>(p =>
        {
            p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run));
            p.Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed++));
            p.Add(c => c.OnInspectSimilar, EventCallback.Factory.Create<RunSelection>(this, s => similar = s));
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-action=inspect-similar]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=inspector-header]"));
        cut.Find("[data-action=inspect-similar]").Click();
        Assert.Equal(run.Id, similar!.Run.Id);
        cut.Find("[data-testid=inspector-close]").Click();
        Assert.Equal(1, closed);
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

public class OverviewRefinementTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public OverviewRefinementTests()
    {
        var clock = new FixedClock(Now);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(new DemoCiProvider(clock));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<IRenderedComponent<RepositoryWorkspace>> Workspace(RepoRef repo, string? defaultBranch = "main")
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, repo); p.Add(c => c.DefaultBranch, defaultBranch); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        return cut;
    }

    private static void ExpandEverything(IRenderedComponent<RepositoryWorkspace> cut)
    {
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
    }

    [Fact]
    public async Task Exactly_three_kpis_and_metadata_row_are_shown()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        Assert.Equal(3, cut.FindAll(".ciw-kpi").Count);
        Assert.Equal("success,attention,duration", string.Join(",", cut.FindAll(".ciw-kpi").Select(k => k.GetAttribute("data-kpi"))));
        var meta = cut.Find("[data-testid=repo-meta]").TextContent;
        Assert.Contains("workflows", meta); Assert.Contains("Latest run", meta); Assert.Contains("default branch", meta);
        Assert.Contains("failing now", cut.Find("[data-kpi=attention]").TextContent);
    }

    [Fact]
    public async Task Workflow_rows_keep_left_chevron_and_status_badge_without_status_icons_or_paths()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var heads = cut.FindAll("[data-workflow] .ciw-wf-head");
        Assert.NotEmpty(heads);
        foreach (var h in heads)
        {
            Assert.NotNull(h.QuerySelector(".ciw-wf-main > .ciw-wf-toggle > svg.ciw-chev"));       // chevron is the first thing in the row
            Assert.NotNull(h.QuerySelector(".ciw-wf-status .ciw-badge"));                         // status badge column
            Assert.Null(h.QuerySelector(".ciw-status"));                                          // no standalone status icon next to names
            Assert.DoesNotContain(".github/workflows", h.TextContent);                            // path is not shown while collapsed
        }
    }

    [Fact]
    public async Task Default_branch_is_not_repeated_but_other_branches_are_shown()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var flags = cut.FindAll(".ciw-wf-head .ciw-flag.branch").Select(f => f.TextContent.Trim()).ToList();
        Assert.DoesNotContain("main", flags);
        Assert.NotEmpty(flags);                                    // fixture has workflows whose latest run is on a feature branch
    }

    [Fact]
    public async Task Recovered_workflow_keeps_success_status_and_is_not_counted_as_failing()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var recovered = cut.FindAll("[data-workflow][data-health=recovered]");
        Assert.NotEmpty(recovered);
        foreach (var r in recovered)
        {
            Assert.Contains("Recovered", r.QuerySelector(".ciw-flags")!.TextContent);
            var badge = r.QuerySelector(".ciw-wf-status .ciw-badge")!;
            Assert.True(badge.ClassList.Contains("ciw-ok") || badge.ClassList.Contains("ciw-active") || badge.ClassList.Contains("ciw-wait") || badge.ClassList.Contains("ciw-muted"));
            Assert.DoesNotContain("Failing now", r.QuerySelector(".ciw-flags")!.TextContent);
        }
        var failingNow = cut.FindAll("[data-workflow][data-health=failingnow]").Count;
        Assert.StartsWith(failingNow.ToString(), cut.Find("[data-kpi=attention] .ciw-kpi-value").TextContent);   // no stale failures in the fixture, so every failing-now row counts
    }

    [Fact]
    public async Task Recurring_failures_are_flagged_on_the_failing_workflow_only()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-workflow] .ciw-flag.bad")), TimeSpan.FromSeconds(5));
        var flagged = cut.FindAll("[data-workflow]").Where(w => w.QuerySelector(".ciw-flags")!.TextContent.Contains("Recurring")).ToList();
        Assert.NotEmpty(flagged);
        Assert.All(flagged, w => Assert.Equal("failingnow", w.GetAttribute("data-health")));
        Assert.All(flagged, w => Assert.Contains("Failed", w.QuerySelector(".ciw-wf-status")!.TextContent));   // primary status is still the real conclusion
    }

    [Fact]
    public async Task Needs_attention_filter_hides_healthy_workflows_and_ranks_failures_first()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        var allCount = cut.FindAll("[data-workflow]").Count;
        cut.Find("[data-filter=Attention]").Click();
        var shown = cut.FindAll("[data-workflow]");
        Assert.NotEmpty(shown);
        Assert.All(shown, w => Assert.Contains(w.GetAttribute("data-health"), new[] { "failingnow", "recovered" }));
        Assert.Contains("Healthy workflows are hidden", cut.Find("[data-testid=attention-note]").TextContent);
        // within every group, no recovered workflow precedes a failing one
        foreach (var g in cut.FindAll("[data-group]"))
        {
            var order = g.QuerySelectorAll("[data-workflow]").Select(w => w.GetAttribute("data-health")).ToList();
            var firstRecovered = order.IndexOf("recovered");
            if (firstRecovered >= 0) Assert.DoesNotContain("failingnow", order.Skip(firstRecovered));
        }
        cut.Find("[data-filter=All]").Click();
        ExpandEverything(cut);
        Assert.True(cut.FindAll("[data-workflow]").Count > shown.Count);               // All is unchanged by the attention view
        Assert.True(cut.FindAll("[data-workflow]").Count >= allCount || allCount < cut.FindAll("[data-workflow]").Count + 1);
    }

    [Fact]
    public async Task Group_order_follows_configuration_not_failures()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        var names = cut.FindAll("[data-group] .ciw-group-name").Select(n => n.TextContent).ToList();
        Assert.Equal("Build & Test", names[0]);
        Assert.Equal(["Build & Test", "Security", "Deployment", "Quality", "Packages", "Documentation", "Maintenance", "AI Agents"], names.Take(8).ToArray());
    }

    [Fact]
    public async Task Search_box_can_be_hidden_when_the_host_has_a_global_search()
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.ShowSearch, false); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll(".ciw-toolbar .ciw-searchbox"));
    }

    [Fact]
    public async Task Collapsed_rows_use_native_buttons_so_enter_and_space_work()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        Assert.All(cut.FindAll(".ciw-group-toggle"), b => { Assert.Equal("button", b.TagName.ToLowerInvariant()); Assert.NotNull(b.GetAttribute("aria-expanded")); });
        Assert.Empty(cut.FindAll(".ciw-group-body"));                                      // collapsed groups render no placeholder rows
    }

    [Fact]
    public void Provider_contract_is_read_only()
    {
        var names = typeof(ICiProvider).GetMethods().Select(m => m.Name).Concat(typeof(ICiProvider).GetProperties().Select(p => p.Name)).ToList();
        Assert.All(names, n => Assert.True(n.StartsWith("List") || n.StartsWith("Get") || n.StartsWith("get_") || n.StartsWith("Check") || n is "Name" or "IsDemo", $"{n} looks like a write operation"));
    }
}

public class ShellTests : BunitContext
{
    private void Arrange()
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
    public void Sidebar_collapses_to_icons_with_tooltips_and_expands_again()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        Assert.Equal("auto", cut.Find(".shell").GetAttribute("data-sidebar"));
        cut.Find("[data-testid=sidebar-toggle]").Click();
        Assert.Equal("collapsed", cut.Find(".shell").GetAttribute("data-sidebar"));
        Assert.Equal("true", cut.Find("nav.sidebar").GetAttribute("data-collapsed"));
        Assert.All(cut.FindAll("a.nav"), a => { Assert.False(string.IsNullOrWhiteSpace(a.GetAttribute("title"))); Assert.NotEmpty(a.QuerySelectorAll(".nav-label")); });   // tooltip + accessible label
        Assert.Equal("false", cut.Find("[data-testid=sidebar-toggle]").GetAttribute("aria-expanded"));
        cut.Find("[data-testid=sidebar-toggle]").Click();
        Assert.Equal("auto", cut.Find(".shell").GetAttribute("data-sidebar"));
    }

    [Fact]
    public async Task Escape_collapses_the_inspector_and_keeps_the_selection()
    {
        Arrange();
        var prefs = Services.GetRequiredService<UiPreferences>();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        prefs.InspectorOpen = true;
        cut.WaitForAssertion(() => Assert.Equal("open", cut.Find(".shell").GetAttribute("data-inspector")));
        await cut.InvokeAsync(() => cut.Instance.OnEscape());
        cut.WaitForAssertion(() => Assert.Equal("closed", cut.Find(".shell").GetAttribute("data-inspector")));
    }

    [Fact]
    public void Inspector_toggle_opens_and_collapses_the_pane()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        Assert.Equal("closed", cut.Find(".shell").GetAttribute("data-inspector"));
        cut.Find("[data-testid=inspector-toggle]").Click();
        Assert.Equal("open", cut.Find(".shell").GetAttribute("data-inspector"));
        cut.Find("[data-testid=inspector-toggle]").Click();
        Assert.Equal("closed", cut.Find(".shell").GetAttribute("data-inspector"));
    }

    [Fact]
    public void Recent_repositories_are_capped_and_link_to_the_full_list()
    {
        Arrange();
        var session = Services.GetRequiredService<AppSession>();
        foreach (var name in new[] { "billing-service", "design-system", "infra-modules", "mobile-app", "data-pipeline", "docs-site" }) session.Select(new RepoRef("example-org", name));
        var cut = Render<CIWorkbench.Web.Components.Layout.SideNav>();
        Assert.InRange(cut.FindAll("a.recent").Count, 1, 4);
        Assert.NotEmpty(cut.FindAll("a.all-repos"));
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
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => inner.GetRunAsync(repo, runId, ct);
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
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => inner.GetRunAsync(repo, runId, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => inner.ListJobsAsync(repo, runId, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct);
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => path == ".github/ciworkbench.yml" ? Task.FromResult<string?>(configYaml) : inner.GetFileAsync(repo, path, ct);
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
        Assert.Contains(notices, n => n.Contains(".github/ciworkbench.yml line 3") && n.Contains("did you mean 'workflows'"));
        Assert.Contains(notices, n => n.Contains("was skipped") && n.Contains("'CI'"));
        Assert.Contains(notices, n => n.Contains("line 7") && n.Contains("missing.yml") && n.Contains("matches no workflow"));

        // the skipped group's workflow is still visible (small repositories open fully); the valid group still applies
        var paths = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        Assert.Contains(".github/workflows/ci.yml", paths);
        Assert.Contains(".github/workflows/quality.yml", paths);
    }
}