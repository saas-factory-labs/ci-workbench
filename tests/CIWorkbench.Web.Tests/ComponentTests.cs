using Bunit;
using Microsoft.AspNetCore.Components;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using CIWorkbench.GitHub;
using CIWorkbench.Web;
using TUnit.Assertions.Enums;
using System.Threading.Tasks;

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
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();
        return cut;
    }

    [Test]
    public async Task Monorepo_renders_groups_collapsed_and_workflows_only_when_expanded()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var all = cut.FindAll("[data-group]");
        await Assert.That(all.Count >= 5).IsTrue();
        var before = cut.FindAll("[data-workflow]").Count;
        cut.Find("[data-group] .ciw-group-head").Click();
        await Assert.That(cut.FindAll("[data-workflow]").Count > before).IsTrue();
    }

    [Test]
    public async Task Expand_state_is_preserved_across_rerender()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var prefs = Services.GetRequiredService<UiPreferences>();
        cut.Find("[data-group] .ciw-group-head").Click();
        var open = prefs.ExpandedKeys.Count(k => k.StartsWith("group|"));
        cut.Render();
        await Assert.That(prefs.ExpandedKeys.Count(k => k.StartsWith("group|"))).IsEqualTo(open);
        await Assert.That(cut.Find("[data-group] .ciw-group-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        foreach (var k in prefs.ExpandedKeys.Where(k => k.StartsWith("group|"))) await Assert.That(k).StartsWith("group|example-org/platform-monorepo|");   // state is per repository
        cut.Find("[data-group] .ciw-group-head").Click();
        await Assert.That(cut.Find("[data-group] .ciw-group-toggle").GetAttribute("aria-expanded")).IsEqualTo("false");
    }

    [Test]
    public async Task Search_filters_workflows_and_status_chip_filters_by_latest_state()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        cut.Find("button[data-filter=Failed]").Click();
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
        var failed = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        await Assert.That(failed).IsNotEmpty();
        await Assert.That(failed).Contains(".github/workflows/container-scan.yml");
        Services.GetRequiredService<WorkspaceSignals>().SetSearch("container-scan");
        await Assert.That(cut.FindAll("[data-workflow]")).HasSingleItem();
    }

    [Test]
    public async Task Actor_repo_shows_attention_summary_and_a_failing_workflow_previews_without_a_run_table()
    {
        RunSelection? picked = null;
        var cut = await RenderWorkspace(DemoCiProvider.ActorRepo, p => p.Add(c => c.OnRunSelected, EventCallback.Factory.Create<RunSelection>(this, s => picked = s)));
        await Assert.That(cut.FindAll("[data-testid=failure-banner]")).IsNotEmpty();
        await Assert.That(cut.FindAll(".ciw-wf.open")).IsEmpty();                                          // nothing expands by itself, even in a tiny repository
        cut.Find("[data-health=failingnow] .ciw-wf-toggle").Click();
        await Assert.That(cut.FindAll("[data-testid=preview-failure]")).IsNotEmpty();
        await Assert.That(cut.FindAll("[data-testid=quick-preview] table")).IsEmpty();                     // a preview is never a run table
        cut.Find("[data-action=quick-inspect]").Click();
        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Repo).IsEqualTo(DemoCiProvider.ActorRepo);
    }

    [Test]
    public async Task Workflow_details_failure_tab_lists_two_recurring_series_and_one_oneoff()
    {
        var provider = Services.GetRequiredService<ICiProvider>();
        var failing = (await provider.ListRecentRunsAsync(DemoCiProvider.ActorRepo, 100, default)).First(r => r.State == RunState.Failure).WorkflowId;
        var cut = Render<WorkflowDetails>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.WorkflowId, failing); p.Add(c => c.Query, new WorkflowDetailsQuery(Tab: "failures")); });
        await Assert.That(cut.FindAll("[data-series]").Count).IsEqualTo(2);
        await Assert.That(cut.FindAll("[data-oneoff]")).HasSingleItem();
        cut.Find("[data-series]").Click();
        await Assert.That(cut.FindAll(".ciw-series-body")).IsNotEmpty();
    }

    private async Task<WorkflowRunInfo> FailedActorRun()
    {
        var provider = Services.GetRequiredService<ICiProvider>();
        return (await provider.ListRecentRunsAsync(DemoCiProvider.ActorRepo, 10, default)).First(r => r.State == RunState.Failure);
    }

    [Test]
    public async Task Inspector_summary_is_failure_first_and_labels_the_detected_error_as_heuristic()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        await Assert.That(cut.FindAll("[data-testid=failure-summary]")).IsNotEmpty();
        await Assert.That(cut.FindAll("[data-testid=cause]")).IsNotEmpty();
        await Assert.That(cut.Find("[data-testid=cause]").TextContent).Contains("Detected error");
        await Assert.That(cut.Find("[data-testid=cause]").TextContent).Contains("not interpreted");
        await Assert.That(cut.FindAll(".ciw-log .ciw-logline.err")).IsNotEmpty();                    // error line is highlighted
        await Assert.That(cut.Find(".ciw-log").TextContent).DoesNotContain("##[error]");         // decorations stripped after the error was identified
        await Assert.That(cut.Markup).Contains("Copy context for coding agent");
        await Assert.That(cut.Find("[data-testid=cause]").TextContent.Replace("detected", "")).DoesNotContain("AI");   // never claims AI analysis
    }

    [Test]
    public async Task Inspector_jobs_tab_lists_failed_jobs_first_and_keeps_successful_jobs()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        await Assert.That(cut.FindAll("[data-tab=Jobs]")).IsNotEmpty();
        cut.Find("[data-tab=Jobs]").Click();
        var jobs = cut.FindAll(".ciw-jobitem");
        await Assert.That(jobs[0].ClassList).Contains("fail");
        await Assert.That(jobs).Contains(j => j.ClassList.Contains("ok"));                         // passed jobs are preserved
        await Assert.That(cut.Find("[data-tab=Jobs]").GetAttribute("aria-selected")).IsEqualTo("true");
    }

    [Test]
    public async Task Inspector_logs_tab_is_bounded_and_has_a_view_full_logs_action_disabled_in_demo()
    {
        var run = await FailedActorRun();
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run)));
        await Assert.That(cut.FindAll("[data-testid=cause]")).IsNotEmpty();
        await Assert.That(cut.Markup).Contains("View full logs");
        await Assert.That(cut.Find(".ciw-insp-actions .ciw-btn.disabled").ClassList).Contains("disabled");
        cut.Find("[data-tab=Logs]").Click();
        await Assert.That(cut.FindAll(".ciw-log .ciw-logline").Count <= 120).IsTrue();
    }

    [Test]
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
        await Assert.That(cut.FindAll("[data-action=inspect-similar]")).IsNotEmpty();
        await Assert.That(cut.FindAll("[data-testid=inspector-header]")).IsNotEmpty();
        cut.Find("[data-action=inspect-similar]").Click();
        await Assert.That(similar!.Run.Id).IsEqualTo(run.Id);
        cut.Find("[data-testid=inspector-close]").Click();
        await Assert.That(closed).IsEqualTo(1);
    }

    [Test]
    public async Task Inspector_without_selection_shows_placeholder() =>
        await Assert.That(Render<RunInspector>().Markup).Contains("Select a run");

    [Test]
    public async Task Density_toggle_switches_preferences_without_reload()
    {
        await Assert.That(Render<DensityToggle>().FindAll("button")[0].GetAttribute("aria-pressed")).IsEqualTo("true");
        var prefs = Services.GetRequiredService<UiPreferences>();
        var cut = Render<DensityToggle>();
        cut.FindAll("button").First(b => b.TextContent == "Compact").Click();
        await Assert.That(prefs.Density).IsEqualTo(Density.Compact);
        await Assert.That(cut.FindAll("button").First(b => b.TextContent == "Compact").ClassList).Contains("on");
    }

    [Test]
    public async Task Failures_view_lists_series_per_workflow()
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.View, WorkspaceView.Failures); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(cut.FindAll("[data-series]").Count).IsEqualTo(2);
    }

    [Test]
    public async Task Components_do_not_depend_on_host_layout_or_routes()
    {
        var asm = typeof(RepositoryWorkspace).Assembly;
        await Assert.That(asm.GetTypes()).DoesNotContain(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), true).Length > 0);
        await Assert.That(asm.GetReferencedAssemblies()).DoesNotContain(a => a.Name!.Contains("SaaS", StringComparison.OrdinalIgnoreCase) || a.Name!.Contains("AppBlueprint", StringComparison.OrdinalIgnoreCase));
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
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();
        return cut;
    }

    private static void ExpandEverything(IRenderedComponent<RepositoryWorkspace> cut)
    {
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
    }

    [Test]
    public async Task Exactly_three_kpis_and_metadata_row_are_shown()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        await Assert.That(cut.FindAll(".ciw-kpi").Count).IsEqualTo(3);
        await Assert.That(string.Join(",", cut.FindAll(".ciw-kpi").Select(k => k.GetAttribute("data-kpi")))).IsEqualTo("success,attention,duration");
        var meta = cut.Find("[data-testid=repo-meta]").TextContent;
        await Assert.That(meta).Contains("workflows"); await Assert.That(meta).Contains("Latest run"); await Assert.That(meta).Contains("default branch");
        await Assert.That(cut.Find("[data-kpi=attention]").TextContent).Contains("failing now");
    }

    [Test]
    public async Task Workflow_rows_keep_left_chevron_and_status_badge_without_status_icons_or_paths()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var heads = cut.FindAll("[data-workflow] .ciw-wf-head");
        await Assert.That(heads).IsNotEmpty();
        foreach (var h in heads)
        {
            await Assert.That(h.QuerySelector(".ciw-wf-main > .ciw-wf-toggle > svg.ciw-chev")).IsNotNull();       // chevron is the first thing in the row
            await Assert.That(h.QuerySelector(".ciw-wf-status .ciw-badge")).IsNotNull();                         // status badge column
            await Assert.That(h.QuerySelector(".ciw-status")).IsNull();                                          // no standalone status icon next to names
            await Assert.That(h.TextContent).DoesNotContain(".github/workflows");                            // path is not shown while collapsed
        }
    }

    [Test]
    public async Task Default_branch_is_not_repeated_but_other_branches_are_shown()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var flags = cut.FindAll(".ciw-wf-head .ciw-flag.branch").Select(f => f.TextContent.Trim()).ToList();
        await Assert.That(flags).DoesNotContain("main");
        await Assert.That(flags).IsNotEmpty();                                    // fixture has workflows whose latest run is on a feature branch
    }

    [Test]
    public async Task Recovered_workflow_keeps_success_status_and_is_not_counted_as_failing()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        var recovered = cut.FindAll("[data-workflow][data-health=recovered]");
        await Assert.That(recovered).IsNotEmpty();
        foreach (var r in recovered)
        {
            await Assert.That(r.QuerySelector(".ciw-flags")!.TextContent).Contains("Recovered");
            var badge = r.QuerySelector(".ciw-wf-status .ciw-badge")!;
            await Assert.That(badge.ClassList.Contains("ciw-ok") || badge.ClassList.Contains("ciw-active") || badge.ClassList.Contains("ciw-wait") || badge.ClassList.Contains("ciw-muted")).IsTrue();
            await Assert.That(r.QuerySelector(".ciw-flags")!.TextContent).DoesNotContain("Failing now");
        }
        var failingNow = cut.FindAll("[data-workflow][data-health=failingnow]").Count;
        await Assert.That(cut.Find("[data-kpi=attention] .ciw-kpi-value").TextContent).StartsWith(failingNow.ToString());   // no stale failures in the fixture, so every failing-now row counts
    }

    [Test]
    public async Task Recurring_failures_are_flagged_on_the_failing_workflow_only()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        ExpandEverything(cut);
        await Assert.That(cut.FindAll("[data-workflow] .ciw-flag.bad")).IsNotEmpty();
        var flagged = cut.FindAll("[data-workflow]").Where(w => w.QuerySelector(".ciw-flags")!.TextContent.Contains("Recurring")).ToList();
        await Assert.That(flagged).IsNotEmpty();
        foreach (var w in flagged)
        {
            await Assert.That(w.GetAttribute("data-health")).IsEqualTo("failingnow");
            await Assert.That(w.QuerySelector(".ciw-wf-status")!.TextContent).Contains("Failed");   // primary status is still the real conclusion
        }
    }

    [Test]
    public async Task Needs_attention_filter_hides_healthy_workflows_and_ranks_failures_first()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        var allCount = cut.FindAll("[data-workflow]").Count;
        cut.Find("[data-filter=Attention]").Click();
        var shown = cut.FindAll("[data-workflow]");
        await Assert.That(shown).IsNotEmpty();
        foreach (var w in shown) await Assert.That(new[] { "failingnow", "recovered" }).Contains(w.GetAttribute("data-health")!);
        await Assert.That(cut.Find("[data-testid=attention-note]").TextContent).Contains("Healthy workflows are hidden");
        // within every group, no recovered workflow precedes a failing one
        foreach (var g in cut.FindAll("[data-group]"))
        {
            var order = g.QuerySelectorAll("[data-workflow]").Select(w => w.GetAttribute("data-health")).ToList();
            var firstRecovered = order.IndexOf("recovered");
            if (firstRecovered >= 0) await Assert.That(order.Skip(firstRecovered)).DoesNotContain("failingnow");
        }
        cut.Find("[data-filter=All]").Click();
        ExpandEverything(cut);
        await Assert.That(cut.FindAll("[data-workflow]").Count > shown.Count).IsTrue();               // All is unchanged by the attention view
        await Assert.That(cut.FindAll("[data-workflow]").Count >= allCount || allCount < cut.FindAll("[data-workflow]").Count + 1).IsTrue();
    }

    [Test]
    public async Task Group_order_follows_configuration_not_failures()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        var names = cut.FindAll("[data-group] .ciw-group-name").Select(n => n.TextContent).ToList();
        await Assert.That(names[0]).IsEqualTo("Build & Test");
        await Assert.That(names.Take(8).ToArray()).IsEquivalentTo(["Build & Test", "Security", "Deployment", "Quality", "Packages", "Documentation", "Maintenance", "AI Agents"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Search_box_can_be_hidden_when_the_host_has_a_global_search()
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.ShowSearch, false); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();
        await Assert.That(cut.FindAll(".ciw-toolbar .ciw-searchbox")).IsEmpty();
    }

    [Test]
    public async Task Collapsed_rows_use_native_buttons_so_enter_and_space_work()
    {
        var cut = await Workspace(DemoCiProvider.Monorepo);
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        foreach (var b in cut.FindAll(".ciw-group-toggle")) { await Assert.That(b.TagName.ToLowerInvariant()).IsEqualTo("button"); await Assert.That(b.GetAttribute("aria-expanded")).IsNotNull(); }
        await Assert.That(cut.FindAll(".ciw-group-body")).IsEmpty();                                      // collapsed groups render no placeholder rows
    }

    [Test]
    public async Task Provider_contract_is_read_only()
    {
        var names = typeof(ICiProvider).GetMethods().Select(m => m.Name).Concat(typeof(ICiProvider).GetProperties().Select(p => p.Name)).ToList();
        await Assert.That(names).All(n => n.StartsWith("List") || n.StartsWith("Get") || n.StartsWith("get_") || n.StartsWith("Check") || n is "Name" or "IsDemo");
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

    [Test]
    public async Task Sidebar_collapses_to_icons_with_tooltips_and_expands_again()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        await Assert.That(cut.Find(".shell").GetAttribute("data-sidebar")).IsEqualTo("auto");
        cut.Find("[data-testid=sidebar-toggle]").Click();
        await Assert.That(cut.Find(".shell").GetAttribute("data-sidebar")).IsEqualTo("collapsed");
        await Assert.That(cut.Find("nav.sidebar").GetAttribute("data-collapsed")).IsEqualTo("true");
        foreach (var a in cut.FindAll("a.nav")) { await Assert.That(string.IsNullOrWhiteSpace(a.GetAttribute("title"))).IsFalse(); await Assert.That(a.QuerySelectorAll(".nav-label")).IsNotEmpty(); }   // tooltip + accessible label
        await Assert.That(cut.Find("[data-testid=sidebar-toggle]").GetAttribute("aria-expanded")).IsEqualTo("false");
        cut.Find("[data-testid=sidebar-toggle]").Click();
        await Assert.That(cut.Find(".shell").GetAttribute("data-sidebar")).IsEqualTo("auto");
    }

    /// <summary>
    /// Navigates on the renderer's dispatcher. The layout reacts to LocationChanged by updating state synchronously but re-rendering through
    /// InvokeAsync; navigating from the test thread and reading the markup straight away raced that render (about 1 run in 12).
    /// </summary>
    private Task Navigate<T>(IRenderedComponent<T> cut, string uri) where T : IComponent =>
        cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo(uri));

    private async Task<WorkflowRunInfo> SelectARun(IRenderedComponent<CIWorkbench.Web.Components.Layout.MainLayout> cut)
    {
        await Navigate(cut, "repo/example-org/review-scraper-actor");
        var run = (await new DemoCiProvider(new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero))).ListRecentRunsAsync(DemoCiProvider.ActorRepo, 10, default)).First(r => r.State == RunState.Failure);
        await cut.InvokeAsync(() => Services.GetRequiredService<InspectorState>().Select(new RunSelection(DemoCiProvider.ActorRepo, run)));
        return run;
    }

    [Test]
    public async Task Inspector_is_absent_by_default_opens_on_selection_closes_on_escape_and_keeps_the_selection()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("closed");
        await Assert.That(cut.FindAll("[data-testid=inspector-pane]")).IsEmpty();                           // no permanent rail or column
        await SelectARun(cut);
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("open");
        await Assert.That(cut.FindAll("[data-testid=quick-inspector]")).IsNotEmpty();
        await cut.InvokeAsync(() => cut.Instance.OnEscape());
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("closed");
        await Assert.That(Services.GetRequiredService<InspectorState>().Selection).IsNotNull();
    }

    [Test]
    public async Task Inspector_can_be_closed_and_reopened_from_the_top_bar()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        await SelectARun(cut);
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("open");
        cut.WaitForAssertion(() => cut.Find("[data-testid=inspector-close]").Click());
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("closed");
        cut.WaitForAssertion(() => cut.Find("[data-testid=inspector-toggle]").Click());
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("open");
        await Assert.That(cut.FindAll("[data-action=open-run-details]")).IsNotEmpty();                     // the quick inspector always links to the full page
    }

    [Test]
    public async Task Navigating_closes_overlays_but_keeps_the_selection_for_the_way_back()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        var run = await SelectARun(cut);
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("open");
        await Navigate(cut, $"repo/example-org/review-scraper-actor/runs/{run.Id}");
        await Assert.That(cut.Find(".shell").GetAttribute("data-inspector")).IsEqualTo("closed");
        await Assert.That(Services.GetRequiredService<InspectorState>().Selection?.Run.Id).IsEqualTo(run.Id);
    }

    [Test]
    public async Task Phone_navigation_is_a_drawer_and_only_one_overlay_is_open_at_a_time()
    {
        Arrange();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        await cut.InvokeAsync(() => cut.Instance.OnViewport(true, false));
        // The toggles' handlers await JS interop, so their re-render can land after Click() returns: wait for the state instead of sampling the DOM once.
        void Expect(string selector, string attribute, string expected) => cut.WaitForAssertion(() =>
        {
            var actual = cut.Find(selector).GetAttribute(attribute);
            if (actual != expected) throw new InvalidOperationException($"{selector} [{attribute}] is '{actual}', expected '{expected}'.");
        });
        Expect(".shell", "data-nav", "closed");
        Expect("[data-testid=sidebar-toggle]", "aria-expanded", "false");
        cut.Find("[data-testid=sidebar-toggle]").Click();
        Expect(".shell", "data-nav", "open");
        Expect("[data-testid=sidebar-toggle]", "aria-expanded", "true");
        cut.WaitForAssertion(() => { if (cut.FindAll("[data-testid=scrim]").Count == 0) throw new InvalidOperationException("scrim missing"); });
        cut.Find("[data-testid=more-toggle]").Click();                                       // opening the actions menu closes the drawer
        Expect(".shell", "data-more", "open");
        Expect(".shell", "data-nav", "closed");
        await cut.InvokeAsync(() => cut.Instance.OnEscape());
        Expect(".shell", "data-more", "closed");
        cut.Find("[data-testid=sidebar-toggle]").Click();
        Expect(".shell", "data-nav", "open");
        await Navigate(cut, "settings");                                                       // following a link closes the drawer
        Expect(".shell", "data-nav", "closed");
    }

    [Test]
    public async Task Phone_repository_picker_lists_full_names_because_the_organisation_picker_is_hidden()
    {
        Arrange();
        var session = Services.GetRequiredService<AppSession>();
        await session.StartDiscoveryAsync();
        var cut = Render<CIWorkbench.Web.Components.Layout.MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        await cut.InvokeAsync(() => cut.Instance.OnViewport(true, false));
        foreach (var o in cut.FindAll("[data-testid=repo-select] option").Where(o => !string.IsNullOrEmpty(o.GetAttribute("value")))) await Assert.That(o.TextContent).Contains("/");
    }

    [Test]
    public async Task Recent_repositories_are_capped_and_link_to_the_full_list()
    {
        Arrange();
        var session = Services.GetRequiredService<AppSession>();
        foreach (var name in new[] { "billing-service", "design-system", "infra-modules", "mobile-app", "data-pipeline", "docs-site" }) session.Select(new RepoRef("example-org", name));
        var cut = Render<CIWorkbench.Web.Components.Layout.SideNav>();
        await Assert.That(cut.FindAll("a.recent").Count).IsBetween(1,4);
        await Assert.That(cut.FindAll("a.all-repos")).IsNotEmpty();
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

    [Test]
    [Arguments("/overview", "Overview")]
    [Arguments("/repositories", "Repositories")]
    [Arguments("/repo/example-org/platform-monorepo", "Workflow Groups")]
    [Arguments("/repo/example-org/platform-monorepo/failures", "Failure Series")]
    [Arguments("/settings", "Settings")]
    public async Task Exactly_one_primary_nav_item_is_active_for_each_route(string path, string expected)
    {
        Arrange();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(path);
        var cut = Render<CIWorkbench.Web.Components.Layout.SideNav>();
        var active = cut.FindAll("a.nav.active");
        await Assert.That(active).HasSingleItem();
        await Assert.That(active[0].TextContent).Contains(expected);
        await Assert.That(active[0].GetAttribute("aria-current")).IsEqualTo("page");
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
    [Test]
    public async Task Selecting_a_workflow_in_a_truncated_window_fetches_one_bounded_page_of_its_own_history()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var spy = new SpyProvider(new DemoCiProvider(clock));
        Services.AddSingleton<TimeProvider>(clock); Services.AddCIWorkbenchComponents(); Services.AddSingleton<ICiProvider>(spy);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // MaxRuns=60 truncates the shared window (the fixture has ~246 runs), so most workflows hold only 0-2 runs in it.
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.MaxRuns, 60); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();
        await Task.Delay(400);
        await Assert.That(spy.WorkflowRunCalls).IsEqualTo(0);                          // the overview never fetches per-workflow history
        var before = spy.WorkflowRunCalls;
        if (cut.FindAll("[data-action=collapse-all]").Count > 0) cut.Find("[data-action=collapse-all]").Click();
        cut.Find("[data-action=expand-all]").Click();
        // Open three workflows (the first-view "best" one may already have its history cached).
        for (var i = 0; i < 3; i++) { cut.FindAll("[data-workflow] .ciw-wf-toggle")[i].Click(); await Task.Delay(150); }
        await Assert.That(spy.WorkflowRunCalls - before).IsBetween(1,3);                 // at most one bounded request per opened workflow, and the cache is used
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
    [Test]
    public async Task Invalid_config_is_explained_with_line_numbers_and_never_hides_a_workflow()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        // line 3 has a typo ('workflow'), and a second group points at a file that does not exist
        const string yaml = "groups:\n  - name: CI\n    workflow: [.github/workflows/ci.yml]\n  - name: Quality\n    workflows:\n      - .github/workflows/quality.yml\n      - .github/workflows/missing.yml\n";
        Services.AddSingleton<TimeProvider>(clock); Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(new ConfigOverrideProvider(new DemoCiProvider(clock), yaml));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.AutoRefresh, false); });
        await Assert.That(cut.FindAll("[data-group]")).IsNotEmpty();

        var notices = cut.WaitForElements(".ciw-notice.warn", TimeSpan.FromSeconds(5)).Select(e => e.TextContent).ToList();
        await Assert.That(notices).Contains(n => n.Contains(".github/ciworkbench.yml line 3") && n.Contains("did you mean 'workflows'"));
        await Assert.That(notices).Contains(n => n.Contains("was skipped") && n.Contains("'CI'"));
        await Assert.That(notices).Contains(n => n.Contains("line 7") && n.Contains("missing.yml") && n.Contains("matches no workflow"));

        // the skipped group's workflow is still visible (small repositories open fully); the valid group still applies
        var paths = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        await Assert.That(paths).Contains(".github/workflows/ci.yml");
        await Assert.That(paths).Contains(".github/workflows/quality.yml");
    }
}