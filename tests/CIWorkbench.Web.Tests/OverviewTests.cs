using System.Net;
using System.Text;
using Bunit;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using CIWorkbench.Web.Components.Layout;
using CIWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace CIWorkbench.Web.Tests;

/// <summary>Answers the few GitHub endpoints the dashboard uses. Never touches the network; every other path is a 404.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string Token = "ghp_unit_test_token_not_real_000000000";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/user/repos"))
            return Task.FromResult(Json("""
                [{"name":"live-api","owner":{"login":"live-org"},"description":"d","default_branch":"main","private":false,"html_url":"https://github.com/live-org/live-api","pushed_at":"2026-10-24T11:00:00Z"},
                 {"name":"live-web","owner":{"login":"live-org"},"description":"d","default_branch":"main","private":false,"html_url":"https://github.com/live-org/live-web","pushed_at":"2026-10-24T10:00:00Z"}]
                """));
        if (path.EndsWith("/actions/workflows"))
            return Task.FromResult(Json("""{"total_count":1,"workflows":[{"id":7,"name":"CI","path":".github/workflows/ci.yml","state":"active","html_url":null}]}"""));
        if (path.EndsWith("/actions/runs"))
            return Task.FromResult(Json("""
                {"total_count":1,"workflow_runs":[{"id":100,"workflow_id":7,"name":"CI","display_title":"Break the build","path":".github/workflows/ci.yml","run_number":5,"run_attempt":1,
                "status":"completed","conclusion":"failure","event":"push","head_branch":"main","head_sha":"abc1234","head_commit":{"message":"Break the build"},"actor":{"login":"octo"},
                "created_at":"2026-10-24T10:00:00Z","run_started_at":"2026-10-24T10:00:00Z","updated_at":"2026-10-24T10:05:00Z","html_url":"https://github.com/live-org/live-api/actions/runs/100"}]}
                """));
        return Task.FromResult(Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
    }
}

/// <summary>Counts what the dashboard asks the provider for, to prove the bounds (no jobs, no logs, bounded repositories).</summary>
internal sealed class DashboardCallCounter(ICiProvider inner) : ICiProvider
{
    public int Workflows, Runs, Jobs, Logs;
    public string Name => inner.Name; public bool IsDemo { get; set; } = true;
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);
    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) { Interlocked.Increment(ref Workflows); return inner.ListWorkflowsAsync(repo, ct); }
    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) { Interlocked.Increment(ref Runs); return inner.ListRecentRunsAsync(repo, max, ct); }
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) => inner.ListWorkflowRunsAsync(repo, workflowId, max, ct);
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => inner.GetRunAsync(repo, runId, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) { Interlocked.Increment(ref Jobs); return inner.ListJobsAsync(repo, runId, ct); }
    // Runner Insights measures with the all-attempts listing; it is a separate, budgeted feature and not part of the triage call budget counted here.
    public Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct) => inner.ListJobAttemptsAsync(repo, runId, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) { Interlocked.Increment(ref Logs); return inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct); }
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => inner.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

public abstract class DashboardContext : BunitContext
{
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    protected DemoCiProvider Demo = null!;

    /// <param name="token">Configure a (fake) server-side token. All GitHub traffic goes to <see cref="FakeGitHub"/>.</param>
    protected void Arrange(bool token = false, bool blockedByExposure = false)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock);
        var config = new ConfigurationBuilder().AddInMemoryCollection(token ? new Dictionary<string, string?> { ["GitHub:Token"] = FakeGitHub.Token } : []).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddCIWorkbenchGitHub(config);
        Services.AddHttpClient(GitHubCiProvider.ApiClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeGitHub());   // last registration wins: no real network
        Services.AddCIWorkbenchComponents();
        Demo = new DemoCiProvider(clock);
        Services.AddScoped<DemoCiProvider>(_ => Demo);
        Services.AddSingleton(new LiveModePolicy(blockedByExposure));
        Services.AddScoped<AppSession>();
        Services.AddScoped<OverviewData>();
        Services.AddRunnerInsights(config);
        Services.PostConfigure<CIWorkbench.Core.Runners.RunnerInsightsOptions>(o => o.DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ciw-tests", Guid.NewGuid().ToString("N")));   // tests never touch the real saved allowances
        Services.AddScoped<ICiProvider, SwitchableCiProvider>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Plain throw for use inside <c>WaitForAssertion</c> (TUnit assertions are awaitable and would never run there).</summary>
    protected static void Must(bool condition) { if (!condition) throw new InvalidOperationException("Condition not met yet."); }

    protected AppSession Session => Services.GetRequiredService<AppSession>();
    protected WorkspaceSignals Signals => Services.GetRequiredService<WorkspaceSignals>();

    protected IRenderedComponent<MainLayout> Shell<TBody>() where TBody : IComponent =>
        Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => { b.OpenComponent<TBody>(0); b.CloseComponent(); })));

    protected static bool RefreshEnabled(IRenderedComponent<MainLayout> cut) => !cut.Find("[data-testid=refresh]").HasAttribute("disabled");
}

public class SessionStateTests : DashboardContext
{
    [Test]
    public async Task Live_without_a_token_stays_in_demo_and_asks_for_setup()
    {
        Arrange(token: false);
        var ok = Session.SetMode(DataMode.Live);
        await Assert.That(ok).IsFalse();
        await Assert.That(Session.Mode).IsEqualTo(DataMode.Demo);
        await Assert.That(Session.LiveSetupRequested).IsTrue();
        Session.SetMode(DataMode.Demo);
        await Assert.That(Session.LiveSetupRequested).IsFalse();
    }

    [Test]
    public async Task Switching_the_source_resets_everything_that_belonged_to_the_old_one()
    {
        Arrange(token: true);
        await Session.StartDiscoveryAsync();
        Session.Select(DemoCiProvider.Monorepo);
        var version = Session.SourceVersion;
        await Assert.That(Session.Recent).IsNotEmpty();

        Session.SetMode(DataMode.Live);
        await Assert.That(Session.SourceVersion).IsEqualTo(version + 1);
        await Assert.That(Session.Selected).IsNull();
        await Assert.That(Session.Recent).IsEmpty();
        await Assert.That(Session.OrgFilter).IsNull();
        await Assert.That(Session.Connection).IsNull();
        await Task.Run(() => SpinWait.SpinUntil(() => Session.Discovery == DiscoveryState.Completed, Wait));
        await Assert.That(Session.Repositories.Select(r => r.Ref.Owner).Distinct()).IsEquivalentTo(new[] { "live-org" });

        Session.SetMode(DataMode.Demo);                                   // and back, predictably
        await Task.Run(() => SpinWait.SpinUntil(() => Session.Discovery == DiscoveryState.Completed && Session.Repositories.Count > 2, Wait));
        await Assert.That(Session.Repositories.Any(r => r.Ref.Equals(DemoCiProvider.Monorepo))).IsTrue();
        await Assert.That(Session.SourceVersion).IsEqualTo(version + 2);
    }

    [Test]
    public async Task A_discovery_that_is_superseded_by_a_mode_switch_never_overwrites_the_new_state()
    {
        Arrange(token: true);
        var stale = Session.StartDiscoveryAsync();                         // demo discovery, pages arrive 30 ms apart
        Session.SetMode(DataMode.Live);                                    // supersedes it
        await stale;
        await Task.Run(() => SpinWait.SpinUntil(() => Session.Discovery == DiscoveryState.Completed, Wait));
        await Assert.That(Session.Discovery).IsEqualTo(DiscoveryState.Completed);                       // not "Cancelled" from the old run
        await Assert.That(Session.Repositories.All(r => r.Ref.Owner == "live-org")).IsTrue();           // no demo repositories leaked in
    }

    [Test]
    public async Task Selecting_a_repository_moves_the_organisation_scope_and_the_scope_raises_changed()
    {
        Arrange();
        var changes = 0; Session.Changed += () => changes++;
        Session.SetOrg("example-labs");
        await Assert.That(Session.OrgFilter).IsEqualTo("example-labs");
        var before = changes;
        Session.SetOrg("example-labs");                                     // no change, no event
        await Assert.That(changes).IsEqualTo(before);
        Session.Select(DemoCiProvider.Monorepo);
        await Assert.That(Session.OrgFilter).IsEqualTo(DemoCiProvider.Owner);
    }

    [Test]
    public async Task Refresh_registration_raises_changed_and_reset_forgets_the_old_source()
    {
        var s = new WorkspaceSignals();
        var raised = 0; s.Changed += () => raised++;
        await Assert.That(s.CanRefresh).IsFalse();
        Action handler = () => { };
        s.RefreshRequested += handler;
        await Assert.That(s.CanRefresh).IsTrue();
        s.ReportUpdated(DateTimeOffset.UnixEpoch); s.SetSearch("x");
        s.Reset();
        await Assert.That(s.LastUpdated).IsNull();
        await Assert.That(s.SearchText).IsNull();
        s.RefreshRequested -= handler;
        await Assert.That(s.CanRefresh).IsFalse();
        await Assert.That(raised).IsGreaterThanOrEqualTo(5);                // subscribe, updated, search, reset, unsubscribe: the top bar is told every time
    }
}

public class OverviewLoadingTests : DashboardContext
{
    [Test]
    public async Task Only_one_batch_is_scanned_with_no_jobs_and_no_logs_and_the_rest_is_never_called_healthy()
    {
        Arrange();
        var counting = new DashboardCallCounter(Demo);
        Services.AddScoped<ICiProvider>(_ => counting);
        await Session.StartDiscoveryAsync();
        var data = Services.GetRequiredService<OverviewData>();
        data.Attach();
        await Task.Run(() => SpinWait.SpinUntil(() => !data.Busy, Wait));

        await Assert.That(data.Snapshots().Count).IsEqualTo(OverviewData.BatchSize);
        await Assert.That(counting.Runs).IsEqualTo(OverviewData.BatchSize);       // one run window per scanned repository
        await Assert.That(counting.Workflows).IsEqualTo(OverviewData.BatchSize);
        await Assert.That(counting.Jobs).IsEqualTo(0);
        await Assert.That(counting.Logs).IsEqualTo(0);
        await Assert.That(data.Remaining).IsEqualTo(Session.Repositories.Count - OverviewData.BatchSize);
        await Assert.That(data.ScopedRepositories().Count(r => data.ScanOf(r.Ref)?.Snapshot is null)).IsEqualTo(data.Remaining);

        data.LoadMore();
        await Task.Run(() => SpinWait.SpinUntil(() => !data.Busy, Wait));
        await Assert.That(data.Snapshots().Count).IsEqualTo(Session.Repositories.Count);
        await Assert.That(counting.Jobs).IsEqualTo(0);
    }

    [Test]
    public async Task Expanding_a_row_loads_one_jobs_request_and_still_no_logs()
    {
        Arrange();
        var counting = new DashboardCallCounter(Demo);
        Services.AddScoped<ICiProvider>(_ => counting);
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-testid=needs-attention] [data-row]").Count > 0), Wait);
        await Assert.That(counting.Jobs).IsEqualTo(0);
        cut.Find("[data-testid=needs-attention] [data-row] .ov-toggle").Click();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-testid=ov-detail]").Count == 1), Wait);
        await Assert.That(counting.Jobs).IsEqualTo(1);
        await Assert.That(counting.Logs).IsEqualTo(0);
    }

    [Test]
    public async Task Refresh_reloads_what_was_scanned_and_keeps_showing_the_old_data_meanwhile()
    {
        Arrange();
        var counting = new DashboardCallCounter(Demo) { IsDemo = false };      // not "demo": runs are cached for 45 s, Refresh must invalidate them
        Services.AddScoped<ICiProvider>(_ => counting);
        await Session.StartDiscoveryAsync();
        var data = Services.GetRequiredService<OverviewData>();
        data.Attach();
        await Task.Run(() => SpinWait.SpinUntil(() => !data.Busy, Wait));
        var runs = counting.Runs;
        data.Refresh();
        await Assert.That(data.Snapshots().Count).IsEqualTo(OverviewData.BatchSize);   // snapshots stay while reloading
        await Task.Run(() => SpinWait.SpinUntil(() => !data.Busy, Wait));
        await Assert.That(counting.Runs).IsEqualTo(runs + OverviewData.BatchSize);
    }
}

public class OverviewPageTests : DashboardContext
{
    [Test]
    public async Task Demo_dashboard_shows_summary_triage_rows_repository_health_and_recent_activity()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-testid=needs-attention] [data-row]").Count > 0), Wait);
        await Assert.That(cut.Find("[data-testid=source-pill]").TextContent).IsEqualTo("Demo data");
        await Assert.That(cut.FindAll("[data-testid=ov-cards] [data-card]").Count).IsEqualTo(5);
        await Assert.That(cut.FindAll("[data-filter]").Select(f => f.GetAttribute("data-filter")!)).IsEquivalentTo(new[] { "NeedsAttention", "FailingNow", "Recurring", "Recovered", "Running", "Unknown" });
        await Assert.That(cut.Find("[data-testid=repo-health]").TextContent).Contains("platform-monorepo");
        await Assert.That(cut.FindAll("[data-testid=recent-activity] [data-run]")).IsNotEmpty();
        await Assert.That(cut.Find("[data-testid=ov-scan]").TextContent).Contains("repositories checked");
        await Assert.That(cut.FindAll("[data-action=load-more]")).IsNotEmpty();                                 // 14 demo repositories > one batch of 12
        cut.Find("[data-action=show-more-repos]").Click();
        await Assert.That(cut.FindAll("[data-repo][data-status=not-checked]")).IsNotEmpty();                    // the unscanned ones say so
    }

    [Test]
    public async Task Cards_and_chips_drive_the_same_filter_and_recurring_rows_are_tagged()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-row]").Count > 0), Wait);
        cut.Find("[data-card=recurring]").Click();
        await Assert.That(cut.Find("[data-filter=Recurring]").GetAttribute("aria-pressed")).IsEqualTo("true");
        await Assert.That(cut.Find("[data-card=recurring]").GetAttribute("aria-pressed")).IsEqualTo("true");
        var rows = cut.FindAll("[data-testid=needs-attention] [data-row]");
        await Assert.That(rows).IsNotEmpty();
        foreach (var r in rows) await Assert.That(r.QuerySelector(".ov-tag.bad")).IsNotNull();
        cut.Find("[data-filter=Running]").Click();
        await Assert.That(cut.Find("[data-filter=Running]").GetAttribute("aria-pressed")).IsEqualTo("true");
        await Assert.That(cut.Find("[data-card=recurring]").GetAttribute("aria-pressed")).IsEqualTo("false");
    }

    [Test]
    public async Task Expanded_row_offers_the_drill_down_links_and_a_bounded_timeline()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-row]").Count > 0), Wait);
        var row = cut.Find("[data-testid=needs-attention] [data-row]");
        row.QuerySelector(".ov-toggle")!.Click();
        var detail = cut.WaitForElement("[data-testid=ov-detail]", Wait);
        await Assert.That(cut.Find(".ov-toggle").GetAttribute("aria-expanded")).IsEqualTo("true");
        foreach (var link in new[] { "repository", "runs", "series", "run" }) await Assert.That(detail.QuerySelector($"[data-link={link}]")).IsNotNull();
        await Assert.That(detail.QuerySelector("[data-link=repository]")!.GetAttribute("href")).StartsWith("repo/");
        await Assert.That(detail.QuerySelector("[data-link=series]")!.GetAttribute("href")).EndsWith("?tab=failures");
        await Assert.That(detail.QuerySelectorAll(".ov-timeline li").Length).IsBetween(1, 5);
        cut.Find(".ov-toggle").Click();
        await Assert.That(cut.FindAll("[data-testid=ov-detail]")).IsEmpty();
    }

    [Test]
    public async Task Empty_filters_say_what_was_checked_instead_of_claiming_everything_is_fine()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-row]").Count > 0), Wait);
        cut.Find("[data-filter=Running]").Click();
        cut.Find("[data-filter=Recovered]").Click();
        var none = cut.FindAll("[data-testid=ov-none]");
        if (none.Count > 0) await Assert.That(none[0].TextContent).Contains("checked so far");                  // 2 repositories are still unchecked
    }

    [Test]
    public async Task Live_without_a_token_shows_the_onboarding_guide_and_keeps_demo_data()
    {
        Arrange(token: false);
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-action=connect]").Count == 1), Wait);
        await Assert.That(cut.FindAll("[data-testid=onboarding]")).IsEmpty();
        cut.Find("[data-action=connect]").Click();
        var guide = cut.WaitForElement("[data-testid=onboarding]", Wait);
        await Assert.That(Session.Mode).IsEqualTo(DataMode.Demo);
        var perms = guide.QuerySelector("[data-testid=onboarding-permissions]")!.TextContent;
        foreach (var p in new[] { "Metadata", "Actions", "Contents" }) await Assert.That(perms).Contains(p);
        await Assert.That(guide.QuerySelectorAll(".ob-level").Count(l => l.TextContent == "Read-only")).IsEqualTo(3);
        await Assert.That(guide.QuerySelector("[data-testid=onboarding-cmd-standalone]")!.TextContent).IsEqualTo("""dotnet user-secrets set "GitHub:Token" "<token>" --project src/CIWorkbench.Web""");
        await Assert.That(guide.QuerySelector("[data-testid=onboarding-cmd-apphost]")!.TextContent).IsEqualTo("""dotnet user-secrets set "Parameters:github-token" "<token>" --project src/CIWorkbench.AppHost""");
        await Assert.That(guide.TextContent).Contains("Restart");
        await Assert.That(guide.QuerySelector("a[href^='https://github.com/settings/personal-access-tokens']")!.GetAttribute("rel")).Contains("noopener");
        await Assert.That(guide.QuerySelector("[data-testid=onboarding-steps]")!.HasAttribute("open")).IsFalse();      // compact on Overview, open in Settings
        cut.Find("[data-action=stay-demo]").Click();
        await Assert.That(cut.FindAll("[data-testid=onboarding]")).IsEmpty();
    }

    [Test]
    public async Task Settings_guide_is_open_when_no_token_and_replaced_by_a_connected_state_when_there_is_one()
    {
        Arrange(token: false);
        var open = Render<LiveOnboarding>();
        await Assert.That(open.Find("[data-testid=onboarding-steps]").HasAttribute("open")).IsTrue();
        await Assert.That(open.FindAll("[data-testid=onboarding-connected]")).IsEmpty();
        await Assert.That(open.FindAll("[data-testid=onboarding-blocked]")).IsEmpty();
    }

    [Test]
    public async Task Connected_state_replaces_the_guide_and_never_renders_the_token()
    {
        Arrange(token: true);
        await Session.StartDiscoveryAsync();
        var cut = Render<LiveOnboarding>();
        await Assert.That(cut.FindAll("[data-testid=onboarding]")).IsEmpty();
        await Assert.That(cut.Find("[data-testid=onboarding-connected]").TextContent).Contains("token configured");
        await Assert.That(cut.FindAll("[data-action=use-live]")).HasSingleItem();
        await Assert.That(cut.Markup).DoesNotContain(FakeGitHub.Token);
        var overview = Render<Overview>();
        await Assert.That(overview.Markup).DoesNotContain(FakeGitHub.Token);
        await Assert.That(overview.Markup).DoesNotContain("ghp_");
    }

    [Test]
    public async Task A_host_that_listens_beyond_localhost_explains_why_live_is_off_instead_of_asking_for_a_token()
    {
        Arrange(token: false, blockedByExposure: true);
        var cut = Render<LiveOnboarding>();
        await Assert.That(cut.FindAll("[data-testid=onboarding-blocked]")).HasSingleItem();
        await Assert.That(cut.FindAll("[data-testid=onboarding-cmd-standalone]")).IsEmpty();
    }

    [Test]
    public async Task Narrow_screens_get_their_own_layout_rules_for_the_dashboard()
    {
        var css = Css.Components;                                    // the dashboard styles ship with the Components package
        var phone = css[css.IndexOf("@media (max-width: 760px)", css.IndexOf(".ov-cards", StringComparison.Ordinal), StringComparison.Ordinal)..];
        foreach (var rule in new[] { ".ov-cards", ".ov-row", ".ov-detail", ".ov-repo-row" }) await Assert.That(phone).Contains(rule);
        await Assert.That(Css.Host).Contains(".ob-perms");           // onboarding is standalone-host only
        await Assert.That(css).Contains("[data-label]::before");        // table headings become inline labels on a phone
    }
}

public class DataSourceSwitchTests : DashboardContext
{
    [Test]
    public async Task Overview_follows_demo_to_live_to_demo_without_navigating_and_refresh_stays_enabled()
    {
        Arrange(token: true);
        var cut = Shell<Overview>();
        cut.WaitForAssertion(() => Must(cut.Find("[data-testid=repo-health]").TextContent.Contains("platform-monorepo")), Wait);
        await Assert.That(cut.Find("[data-testid=source-pill]").TextContent).IsEqualTo("Demo data");
        await Assert.That(RefreshEnabled(cut)).IsTrue();                                  // no repository is selected, yet Refresh works on the dashboard

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Live));
        cut.WaitForAssertion(() => Must(cut.Find("[data-testid=repo-health]").TextContent.Contains("live-api")), Wait);
        await Assert.That(cut.Find("[data-testid=source-pill]").TextContent).IsEqualTo("Live GitHub");
        await Assert.That(cut.Markup).DoesNotContain("platform-monorepo");                // nothing of the old source lingers
        await Assert.That(RefreshEnabled(cut)).IsTrue();
        cut.WaitForAssertion(() => Must(cut.Find("[data-testid=needs-attention]").TextContent.Contains("Break the build")), Wait);
        cut.WaitForAssertion(() => Must(cut.Find(".updated").TextContent.Contains("Updated")), Wait);

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Demo));
        cut.WaitForAssertion(() => Must(cut.Find("[data-testid=repo-health]").TextContent.Contains("platform-monorepo")), Wait);
        await Assert.That(cut.Find("[data-testid=source-pill]").TextContent).IsEqualTo("Demo data");
        await Assert.That(cut.Markup).DoesNotContain("live-api");
        await Assert.That(RefreshEnabled(cut)).IsTrue();
    }

    [Test]
    public async Task Refresh_is_enabled_only_while_a_page_can_refresh_and_tells_the_top_bar_when_that_changes()
    {
        Arrange();
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "no refreshable page"))));
        await Assert.That(RefreshEnabled(cut)).IsFalse();
        Action handler = () => { };
        await cut.InvokeAsync(() => Signals.RefreshRequested += handler);
        cut.WaitForAssertion(() => Must(RefreshEnabled(cut)));
        await cut.InvokeAsync(() => Signals.RefreshRequested -= handler);
        cut.WaitForAssertion(() => Must(!RefreshEnabled(cut)));
    }

    [Test]
    public async Task Repository_page_reloads_from_the_new_source_and_says_when_the_repository_is_not_part_of_it()
    {
        Arrange(token: true);
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b =>
        {
            b.OpenComponent<RepoPage>(0);
            b.AddComponentParameter(1, nameof(RepoPage.Owner), DemoCiProvider.Owner);
            b.AddComponentParameter(2, nameof(RepoPage.Name), DemoCiProvider.MonorepoName);
            b.CloseComponent();
        })));
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-group]").Count > 0), Wait);
        await Assert.That(Session.Selected).IsEqualTo(DemoCiProvider.Monorepo);
        await Assert.That(RefreshEnabled(cut)).IsTrue();

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Live));
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-testid=not-in-source]").Count == 1), Wait);   // reloaded without navigating away and back
        cut.WaitForAssertion(() => Must(!cut.Markup.Contains("CI: API")), Wait);                                       // no demo workflows left on screen
        await Assert.That(Session.Selected).IsEqualTo(DemoCiProvider.Monorepo);                                        // selection is re-established, not left null
        await Assert.That(RefreshEnabled(cut)).IsTrue();

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Demo));
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-group]").Count > 0), Wait);
        await Assert.That(cut.FindAll("[data-testid=not-in-source]")).IsEmpty();
    }

    [Test]
    public async Task Workspace_reloads_when_only_the_data_source_key_changes()
    {
        Arrange();
        var counting = new DashboardCallCounter(Demo);
        Services.AddScoped<ICiProvider>(_ => counting);
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.AutoRefresh, false); p.Add(c => c.SourceKey, 0); });
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-group]").Count > 0), Wait);
        var loads = counting.Workflows;

        cut.Render(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.AutoRefresh, false); p.Add(c => c.SourceKey, 0); });   // parent re-render: no reload
        await Assert.That(counting.Workflows).IsEqualTo(loads);

        counting.IsDemo = false;                                                                     // the provider behind the cache is now another source
        cut.Render(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.AutoRefresh, false); p.Add(c => c.SourceKey, 1); });
        cut.WaitForAssertion(() => Must(counting.Workflows == (loads + 1)), Wait);
    }
}
