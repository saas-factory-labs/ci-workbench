using Bunit;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using CIWorkbench.Core.Runners;
using CIWorkbench.GitHub;
using CIWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace CIWorkbench.Web.Tests;

/// <summary>Demo provider with switches for the failure modes Runner Insights must survive, and counters for what it fetched.</summary>
internal sealed class ProbeProvider(ICiProvider inner) : ICiProvider
{
    public int JobAttemptCalls;
    public bool FailRuns, DenyVariables;
    public string? HiddenWorkflow, UnreadableFile;
    public WorkflowInfo? ExtraWorkflow;
    public string Name => inner.Name;
    public bool IsDemo => true;
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);
    public async Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) => (await inner.ListWorkflowsAsync(repo, ct)).Where(w => w.Path != HiddenWorkflow).Concat(ExtraWorkflow is null ? [] : [ExtraWorkflow]).ToList();
    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) => FailRuns ? throw new CiProviderException("GitHub rate limit reached (403).", 403) : inner.ListRecentRunsAsync(repo, max, ct);
    public Task<RunPeriod> ListRunsSinceAsync(RepoRef repo, DateTimeOffset since, int maxRuns, CancellationToken ct) => FailRuns ? throw new CiProviderException("GitHub rate limit reached (403).", 403) : inner.ListRunsSinceAsync(repo, since, maxRuns, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) => inner.ListWorkflowRunsAsync(repo, workflowId, max, ct);
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => inner.GetRunAsync(repo, runId, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => inner.ListJobsAsync(repo, runId, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct) { Interlocked.Increment(ref JobAttemptCalls); return inner.ListJobAttemptsAsync(repo, runId, ct); }
    public Task<ActionsVariables> GetActionsVariablesAsync(RepoRef repo, CancellationToken ct) =>
        DenyVariables ? Task.FromResult(new ActionsVariables(VariablesAccess.PermissionMissing, new Dictionary<string, string>(), "Needs Variables: Read-only.")) : inner.GetActionsVariablesAsync(repo, ct);
    public Task<BillingUsage> GetBillingUsageAsync(string owner, string? ownerType, int year, int month, CancellationToken ct) => inner.GetBillingUsageAsync(owner, ownerType, year, month, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => inner.GetJobLogTailAsync(repo, jobId, maxBytes, ct);
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => path == UnreadableFile ? throw new CiProviderException("forbidden", 403) : inner.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

public class RunnerInsightsWebTests : DashboardContext
{
    private static bool Has<T>(IRenderedComponent<T> cut, string selector) where T : IComponent => cut.FindAll(selector).Count > 0;

    /// <summary>Renders the Overview and waits for the first (budgeted) collection pass: Partial by design.</summary>
    private async Task<IRenderedComponent<Overview>> FirstPass(Func<ICiProvider, ProbeProvider>? make = null)
    {
        Arrange();
        if (make is not null) { var probe = make(Demo); Services.AddScoped<ICiProvider>(_ => probe); }
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        var data = Services.GetRequiredService<RunnerInsightsData>();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=ru-grid]") && !data.IsMeasuring), Wait);
        return cut;
    }

    /// <summary>Scans every repository and keeps pressing "Collect remaining" until nothing is missing.</summary>
    private async Task<IRenderedComponent<Overview>> Complete(Func<ICiProvider, ProbeProvider>? make = null)
    {
        var cut = await FirstPass(make);
        var data = Services.GetRequiredService<RunnerInsightsData>(); var ov = Services.GetRequiredService<OverviewData>();
        await cut.InvokeAsync(ov.LoadMore);
        cut.WaitForAssertion(() =>
        {
            if (!data.IsMeasuring && !ov.Busy && data.PendingRuns > 0) cut.InvokeAsync(data.CollectRemaining);
            Must(!data.IsMeasuring && !ov.Busy && data.PendingRuns == 0 && data.Report.Coverage.Overall == Coverage.Complete);
        }, TimeSpan.FromSeconds(30));
        return cut;
    }

    // ------------------------------------------------------------ Overview: completeness

    [Test]
    public async Task The_first_budgeted_pass_is_partial_prominent_and_withholds_remaining_allowances()
    {
        var cut = await FirstPass();
        await Assert.That(cut.Find("[data-testid=ru-coverage]").TextContent).IsEqualTo("Partial");
        await Assert.That(cut.Find("[data-testid=ru-meta]").TextContent).Contains("runs collected");
        await Assert.That(cut.FindAll("[data-action=collect-remaining]").Count).IsEqualTo(1);            // explicit user action to continue
        var others = cut.FindAll(".ru-card:not([data-provider=github]) [data-testid=ru-allowance]");
        await Assert.That(others.Any(a => a.TextContent.Contains("left"))).IsFalse();                    // no remaining from incomplete history
        await Assert.That(cut.FindAll("[data-testid=ru-main-block] .ru-value").All(v => v.TextContent.StartsWith('≥'))).IsTrue();   // "at least"
        var markup = cut.Markup;
        await Assert.That(markup.IndexOf("data-testid=\"repo-health\"", StringComparison.Ordinal)).IsLessThan(markup.IndexOf("data-testid=\"runner-usage\"", StringComparison.Ordinal));
    }

    [Test]
    public async Task Collecting_everything_completes_the_period_and_unlocks_estimated_remaining()
    {
        var cut = await Complete();
        await Assert.That(cut.Find("[data-testid=ru-coverage]").TextContent).IsEqualTo("Complete");
        var providers = cut.FindAll("[data-testid=ru-grid] .ru-card").Select(c => c.GetAttribute("data-provider")).ToArray();
        await Assert.That(providers).Contains("github").And.Contains("tenki").And.Contains("avrea").And.Contains("buildpulse").And.Contains("celesto").And.Contains("hetzner-arc").And.Contains("blacksmith").And.Contains("self-hosted");
        await Assert.That(cut.Find("[data-provider=avrea] [data-testid=ru-allowance]").TextContent).Contains("left").And.Contains("estimated");
        await Assert.That(cut.Find("[data-provider=tenki] progress").GetAttribute("max")).IsEqualTo("1");
        await Assert.That(cut.Find("[data-provider=celesto] [data-testid=ru-allowance]").TextContent).Contains("Remaining unavailable");
        await Assert.That(cut.Find("[data-provider=blacksmith] [data-testid=ru-allowance]").TextContent).Contains("Allowance unknown");
        await Assert.That(cut.Find("[data-provider=hetzner-arc]").TextContent).Contains("cost not tracked").And.Contains("No allowance (self-managed)");
    }

    [Test]
    public async Task Github_billing_and_observed_usage_are_separate_measurements_with_their_own_labels()
    {
        var cut = await FirstPass();
        var org = cut.Find("[data-provider=github] [data-pool=example-org]");
        var billing = org.QuerySelector("[data-testid=ru-billing-block]")!; var observed = org.QuerySelector("[data-testid=ru-observed-block]")!;
        await Assert.That(billing.TextContent).Contains("Verified").And.Contains("2,715").And.Contains("net $3.78").And.Contains("Free minutes used up");
        await Assert.That(observed.TextContent).Contains("Estimated").And.DoesNotContain("Verified");
        await Assert.That(observed.TextContent).DoesNotContain("2,715");
        var labs = cut.Find("[data-provider=github] [data-pool=example-labs]");
        await Assert.That(labs.QuerySelector("[data-testid=ru-billing-block]")!.TextContent).Contains("Missing permission");
        await Assert.That(labs.QuerySelector("[data-testid=ru-allowance]")!.TextContent).Contains("Allowance unknown");
    }

    [Test]
    public async Task Official_account_usage_gives_remaining_even_while_job_collection_is_partial()
    {
        var cut = await FirstPass();
        var allowance = cut.Find("[data-provider=github] [data-pool=example-org] [data-testid=ru-allowance]").TextContent;
        await Assert.That(allowance).Contains("of 2,000 min used");                         // the demo account used more than its 2,000 included minutes, per official usage
        await Assert.That(allowance).Contains("0 min left");
    }

    [Test]
    public async Task Collection_resumes_without_refetching_finished_jobs()
    {
        ProbeProvider? probe = null;
        var cut = await Complete(d => probe = new ProbeProvider(d));
        var fetched = probe!.JobAttemptCalls;
        await Assert.That(fetched).IsGreaterThan(100);                                      // more than one budgeted pass was needed
        var data = Services.GetRequiredService<RunnerInsightsData>();
        await cut.InvokeAsync(data.Refresh);
        cut.WaitForAssertion(() => Must(!data.IsMeasuring), Wait);
        await Assert.That(probe.JobAttemptCalls).IsEqualTo(fetched);                        // refresh re-lists runs but every finished job is already known
        await Assert.That(data.Report.Coverage.Overall).IsEqualTo(Coverage.Complete);
    }

    [Test]
    public async Task Jobs_are_not_counted_twice_across_passes()
    {
        var cut = await Complete();
        var data = Services.GetRequiredService<RunnerInsightsData>();
        var jobs = data.Report.Providers.Sum(p => p.Jobs);
        await cut.InvokeAsync(data.Refresh);
        cut.WaitForAssertion(() => Must(!data.IsMeasuring), Wait);
        await Assert.That(data.Report.Providers.Sum(p => p.Jobs)).IsEqualTo(jobs);
        await Assert.That(data.Report.Excluded.GetValueOrDefault(ExclusionReason.Duplicate)).IsEqualTo(0);
    }

    [Test]
    public async Task Overview_without_data_yet_shows_no_providers()
    {
        Arrange();
        var cut = Render<Overview>();
        await Assert.That(cut.FindAll("[data-testid=ru-grid]")).IsEmpty();                // nothing is invented before data arrives
    }

    [Test]
    public async Task A_data_source_switch_drops_runner_data_of_the_other_source()
    {
        Arrange(token: true);
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=ru-grid] [data-pool=example-org]")), Wait);

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Live));
        cut.WaitForAssertion(() => Must(cut.Find("[data-testid=repo-health]").TextContent.Contains("live-api")), Wait);
        await Assert.That(cut.FindAll("[data-pool=example-org]")).IsEmpty();               // demo accounts never leak into live
        await Assert.That(cut.Markup).DoesNotContain("example-org");

        await cut.InvokeAsync(() => Session.SetMode(DataMode.Demo));
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=ru-grid] [data-pool=example-org]")), Wait);
        await Assert.That(cut.Markup).DoesNotContain("live-org");
    }

    [Test]
    public async Task Org_scope_narrows_what_is_shown_to_that_account()
    {
        var cut = await Complete();
        await cut.InvokeAsync(() => Session.SetOrg("example-labs"));
        cut.WaitForAssertion(() => Must(!Has(cut, "[data-provider=tenki]") && Has(cut, "[data-pool=example-labs]")), Wait);   // example-labs has no Tenki jobs
        await Assert.That(cut.FindAll("[data-pool=example-org]")).IsEmpty();
    }

    [Test]
    public async Task Collection_pauses_at_the_rate_limit_reserve_and_says_how_to_continue()
    {
        Arrange(token: true);
        Session.SetMode(DataMode.Live);
        var tracker = Services.GetRequiredService<RateLimitTracker>();
        typeof(RateLimitTracker).GetProperty(nameof(RateLimitTracker.Last))!.SetValue(tracker, new RateLimitInfo(5000, 10, new DateTimeOffset(2026, 10, 24, 13, 0, 0, TimeSpan.Zero)));
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        var data = Services.GetRequiredService<RunnerInsightsData>();
        cut.WaitForAssertion(() => Must(!data.IsMeasuring && data.Note is not null), Wait);
        await Assert.That(data.Note).Contains("Paused").And.Contains("reserve");
        await Assert.That(data.Report.Providers).IsEmpty();                               // nothing was fetched, so nothing is invented
    }

    // ------------------------------------------------------------ Settings: allowances

    private async Task<IRenderedComponent<RunnerInsightsSettings>> Settings()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<RunnerInsightsSettings>();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-plan='github.example-org']")), Wait);
        return cut;
    }

    [Test]
    public async Task Settings_is_organised_in_compact_sections_with_one_allowance_row_per_provider_and_per_github_account()
    {
        var cut = await Settings();
        foreach (var s in new[] { "providers", "allowances", "collection", "advanced" }) await Assert.That(Has(cut, $"[data-testid=ri-sec-{s}]")).IsTrue();
        await Assert.That(cut.Find("[data-testid=ri-sec-advanced]").HasAttribute("open")).IsFalse();      // methodology is collapsed by default
        await Assert.That(cut.Find("[data-plan='github.example-org']").TextContent).Contains("Confirmed");
        await Assert.That(cut.Find("[data-plan='github.example-labs']").TextContent).Contains("Allowance unknown");
        await Assert.That(cut.Find("[data-plan=tenki]").TextContent).Contains("Unconfirmed");
        await Assert.That(cut.Find("[data-plan=hetzner-arc]").TextContent).Contains("No allowance (self-managed)");
    }

    [Test]
    public async Task Settings_lists_billing_status_per_account_with_the_missing_permission()
    {
        Arrange();
        await Session.StartDiscoveryAsync();
        var cut = Render<Overview>();
        var data = Services.GetRequiredService<RunnerInsightsData>();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=ru-grid]") && !data.IsMeasuring), Wait);
        var settings = Render<RunnerInsightsSettings>();
        await Assert.That(settings.Find("[data-testid=ri-billing] [data-owner=example-labs]").TextContent).Contains("Missing permission").And.Contains("Administration: Read-only");
        await Assert.That(settings.Find("[data-testid=ri-billing] [data-owner=example-org]").TextContent).Contains("Verified");
        await Assert.That(settings.Markup).Contains("Plan: Read-only").And.Contains("Variables: Read-only");
    }

    [Test]
    public async Task A_suggestion_prefills_the_form_but_is_never_saved_or_confirmed_by_itself()
    {
        var cut = await Settings();
        cut.Find("[data-plan=blacksmith] [data-action=edit-plan]").Click();
        await Assert.That(cut.Find("[data-testid=ri-suggestions]").TextContent).Contains("not verified for your account");
        cut.Find("[data-testid=ri-suggestions] button").Click();
        await Assert.That(cut.Find("[data-field=amount]").GetAttribute("value")).IsEqualTo("3000");
        await Assert.That(cut.Find("[data-field=confirmed]").HasAttribute("checked")).IsFalse();
        await Assert.That(cut.Find("[data-plan=blacksmith]").GetAttribute("data-source")).IsEqualTo("none");   // nothing saved until the user saves
    }

    [Test]
    public async Task An_allowance_can_be_validated_saved_edited_and_reset()
    {
        var cut = await Settings();
        var data = Services.GetRequiredService<RunnerInsightsData>();
        cut.Find("[data-plan=blacksmith] [data-action=edit-plan]").Click();
        cut.Find("[data-field=kind]").Change(nameof(AllowanceKind.MonthlyIncluded));
        cut.Find("[data-testid=ri-editor]").Submit();                                                     // amount missing
        await Assert.That(cut.Find("[data-testid=ri-errors]").TextContent).Contains("positive number");

        cut.Find("[data-field=amount]").Change("3000");
        cut.Find("[data-field=confirmed]").Change(true);
        cut.Find("[data-testid=ri-editor]").Submit();
        cut.WaitForAssertion(() => Must(cut.Find("[data-plan=blacksmith]").GetAttribute("data-source") == "saved"), Wait);
        await Assert.That(cut.Find("[data-plan=blacksmith]").TextContent).Contains("Monthly included").And.Contains("3,000").And.Contains("Confirmed");
        await Assert.That(data.Options.PlanOf("blacksmith")!.AllowanceAmount).IsEqualTo(3000d);           // effective immediately

        cut.Find("[data-plan=blacksmith] [data-action=reset-plan]").Click();
        await Assert.That(cut.Find("[data-plan=blacksmith]").GetAttribute("data-source")).IsEqualTo("none");
        await Assert.That(data.Options.PlanOf("blacksmith")).IsNull();
    }

    [Test]
    public async Task One_time_credits_and_prepaid_balances_need_a_balance_with_a_date()
    {
        var cut = await Settings();
        cut.Find("[data-plan=celesto] [data-action=edit-plan]").Click();
        cut.Find("[data-field=kind]").Change(nameof(AllowanceKind.Prepaid));
        cut.Find("[data-field=balance]").Change("20");
        cut.Find("[data-testid=ri-editor]").Submit();
        await Assert.That(cut.Find("[data-testid=ri-errors]").TextContent).Contains("when you read the balance");
        cut.Find("[data-field=asof]").Change("2026-10-20T08:00");
        cut.Find("[data-testid=ri-editor]").Submit();
        cut.WaitForAssertion(() => Must(cut.Find("[data-plan=celesto]").TextContent.Contains("balance")), Wait);
        await Assert.That(cut.Find("[data-plan=celesto]").TextContent).Contains("Prepaid balance").And.Contains("20 min");
    }

    [Test]
    public async Task Server_configuration_wins_over_saved_allowances_and_cannot_be_overwritten_from_the_ui()
    {
        Arrange(token: true);
        Services.Configure<RunnerInsightsOptions>(o => o.Providers["avrea"] = new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 1234, Confirmed = true });
        Session.SetMode(DataMode.Live);
        var data = Services.GetRequiredService<RunnerInsightsData>();
        var store = Services.GetRequiredService<RunnerPlanStore>();
        store.Save(RunnerJobStore.ContextOf(DataMode.Live, Services.GetRequiredService<IOptions<GitHubOptions>>().Value), "avrea", new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 1 });
        await Assert.That(data.SourceOf("avrea")).IsEqualTo(PlanSource.Server);
        await Assert.That(data.Options.PlanOf("avrea")!.AllowanceAmount).IsEqualTo(1234d);
        await Assert.That(data.SavePlan("avrea", new ProviderPlanOptions { Allowance = AllowanceKind.Unlimited })).IsNotEmpty();
        await Assert.That(data.ResetPlan("avrea")).IsFalse();
        await Assert.That(data.SourceOf("tenki")).IsEqualTo(PlanSource.None);               // live mode has no built-in plans
    }

    [Test]
    public async Task Live_allowances_are_kept_per_credential_scope_and_never_mix_with_the_demo()
    {
        Arrange(token: true);
        Session.SetMode(DataMode.Live);
        var data = Services.GetRequiredService<RunnerInsightsData>();
        var store = Services.GetRequiredService<RunnerPlanStore>();
        await Assert.That(data.SavePlan("tenki", new ProviderPlanOptions { Allowance = AllowanceKind.Unlimited })).IsEmpty();
        var mine = RunnerJobStore.ContextOf(DataMode.Live, Services.GetRequiredService<IOptions<GitHubOptions>>().Value);
        await Assert.That(store.Has(mine, "tenki")).IsTrue();
        await Assert.That(store.Has(RunnerJobStore.ContextOf(DataMode.Live, new GitHubOptions { Token = "another-token" }), "tenki")).IsFalse();
        Session.SetMode(DataMode.Demo);
        await Assert.That(data.Options.PlanOf("tenki")!.Allowance).IsEqualTo(AllowanceKind.MonthlyIncluded);   // the demo plan, not the live one
    }

    // ------------------------------------------------------------ Repository

    private async Task<IRenderedComponent<RepoRunners>> Repo(RepoRef repo, Func<ICiProvider, ProbeProvider>? make = null)
    {
        Arrange();
        if (make is not null) { var probe = make(Demo); Services.AddScoped<ICiProvider>(_ => probe); }
        await Session.StartDiscoveryAsync();
        var cut = Render<RepoRunners>(p => p.Add(c => c.Repo, repo));
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=rr-workflows]") || Has(cut, "[data-testid=rr-none]") || Has(cut, "[data-testid=rr-error]")), Wait);
        return cut;
    }

    [Test]
    public async Task Repository_runners_group_by_provider_and_compare_configured_with_actual()
    {
        var cut = await Repo(DemoCiProvider.Monorepo);
        await Assert.That(cut.FindAll("[data-testid=rr-usage] .rr-group").Count).IsGreaterThan(2);
        var ci = cut.Find("[data-workflow='.github/workflows/ci-api.yml']");
        await Assert.That(ci.TextContent).Contains("avrea-ubuntu-latest").And.Contains("configured").And.Contains("tenki-standard-small-2c-4g").And.Contains("potential fallback");
        await Assert.That(ci.QuerySelector(".rr-chip.fallback")!.TextContent).Contains("tenki-standard-small-2c-4g");
        await Assert.That(ci.QuerySelector("a.rr-chip.actual")!.GetAttribute("href")).Contains("/runs/");
        await Assert.That(cut.Find("[data-workflow='.github/workflows/codeql.yml']").TextContent).Contains("hetzner-arc").And.Contains("Ran as configured");
        await Assert.That(cut.Find("[data-testid=rr-shared]").TextContent).Contains("whole example-org account").And.Contains("Estimated");
    }

    [Test]
    public async Task Repository_collection_is_partial_until_the_user_collects_the_rest()
    {
        var cut = await Repo(DemoCiProvider.Monorepo);
        await Assert.That(cut.Find("[data-testid=rr-coverage]").TextContent).IsEqualTo("Partial");
        await Assert.That(cut.Find("[data-testid=rr-usage] .rr-gsum").TextContent).StartsWith("≥");
        cut.Find("[data-action=collect-more-runners]").Click();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=rr-coverage]") && cut.Find("[data-testid=rr-coverage]").TextContent == "Complete"), TimeSpan.FromSeconds(20));
        await Assert.That(cut.FindAll("[data-action=collect-more-runners]")).IsEmpty();
    }

    [Test]
    public async Task A_workflow_missing_from_the_current_list_is_historical_and_an_unreadable_file_is_not_an_invalid_runner()
    {
        var cut = await Repo(DemoCiProvider.Monorepo, d => new ProbeProvider(d) { HiddenWorkflow = ".github/workflows/codeql.yml", UnreadableFile = ".github/workflows/lint.yml" });
        cut.Find("[data-action=collect-more-runners]").Click();
        cut.WaitForAssertion(() => Must(Has(cut, "[data-testid=rr-coverage]") && cut.Find("[data-testid=rr-coverage]").TextContent == "Complete"), TimeSpan.FromSeconds(20));
        var historical = cut.Find("[data-workflow='.github/workflows/codeql.yml']");
        await Assert.That(historical.GetAttribute("data-historical")).IsEqualTo("true");
        await Assert.That(historical.TextContent).Contains("not in current workflows").And.Contains("Historical only").And.DoesNotContain("not read");
        var unreadable = cut.Find("[data-workflow='.github/workflows/lint.yml']");
        await Assert.That(unreadable.TextContent).Contains("definition not read").And.Contains("Configuration not checked");
        await Assert.That(unreadable.TextContent).DoesNotContain("invalid").And.DoesNotContain("missing");
    }

    [Test]
    public async Task Github_managed_dynamic_workflows_have_no_file_and_are_not_reported_as_unreadable()
    {
        var cut = await Repo(DemoCiProvider.Monorepo, d => new ProbeProvider(d) { ExtraWorkflow = new WorkflowInfo(99, "Copilot code review", "dynamic/copilot-pull-request-reviewer/copilot-pull-request-reviewer", true, null) });
        var row = cut.Find("[data-workflow='dynamic/copilot-pull-request-reviewer/copilot-pull-request-reviewer']");
        await Assert.That(row.TextContent).Contains("GitHub-managed workflow").And.DoesNotContain("definition not read");
    }

    [Test]
    public async Task Missing_variables_permission_marks_configured_runners_unknown_instead_of_guessing()
    {
        var cut = await Repo(DemoCiProvider.Monorepo, d => new ProbeProvider(d) { DenyVariables = true });
        await Assert.That(Has(cut, "[data-testid=rr-vars-missing]")).IsTrue();
        var ci = cut.Find("[data-workflow='.github/workflows/ci-api.yml']");
        await Assert.That(ci.TextContent).Contains("unknown (dynamic)");
        await Assert.That(ci.QuerySelector(".rr-chip.configured")).IsNull();
    }

    [Test]
    public async Task Public_repository_runner_time_is_measured_but_draws_no_github_allowance()
    {
        var cut = await Repo(new RepoRef("example-labs", "mobile-app"));
        await Assert.That(cut.Find("[data-testid=rr-shared]").TextContent).Contains("Public repository");
        var github = cut.Find("[data-testid=rr-usage] [data-provider=github]");
        await Assert.That(github.TextContent).Contains("0 min");                          // standard runners in a public repository are free
    }

    [Test]
    public async Task Repository_without_finished_jobs_shows_an_empty_state()
    {
        var cut = await Repo(new RepoRef("example-org", "design-system"));
        await Assert.That(Has(cut, "[data-testid=rr-none]")).IsTrue();
    }

    [Test]
    public async Task Repository_runners_surface_a_load_error_with_a_retry()
    {
        var cut = await Repo(DemoCiProvider.Monorepo, d => new ProbeProvider(d) { FailRuns = true });
        await Assert.That(cut.Find("[data-testid=rr-error]").TextContent).Contains("rate limit");
    }

    [Test]
    public async Task Live_repository_runners_wait_for_a_click()
    {
        Arrange(token: true);
        Session.SetMode(DataMode.Live);
        var cut = Render<RepoRunners>(p => p.Add(c => c.Repo, new RepoRef("live-org", "live-api")));
        await Assert.That(cut.FindAll("[data-testid=rr-start]").Count).IsEqualTo(1);       // nothing is requested until asked
        cut.Find("[data-action=load-runners]").Click();
        cut.WaitForAssertion(() => Must(cut.FindAll("[data-testid=rr-start]").Count == 0 && !cut.Markup.Contains("Reading workflow files")), Wait);
    }

    // ------------------------------------------------------------ isolation

    [Test]
    public async Task Job_cache_is_partitioned_by_data_source_credentials_and_allowlist()
    {
        var a = new GitHubOptions { Token = "token-a" };
        var b = new GitHubOptions { Token = "token-b" };
        var allow = new GitHubOptions { Token = "token-a", AllowedRepositories = ["acme/*"] };
        var keys = new[] { RunnerJobStore.ContextOf(DataMode.Demo, a), RunnerJobStore.ContextOf(DataMode.Live, a), RunnerJobStore.ContextOf(DataMode.Live, b), RunnerJobStore.ContextOf(DataMode.Live, allow) };
        await Assert.That(keys.Distinct().Count()).IsEqualTo(4);
        await Assert.That(keys[1]).DoesNotContain("token-a");                              // only a fingerprint, never the token
        await Assert.That(RunnerJobStore.ContextOf(DataMode.Demo, a)).IsEqualTo(RunnerJobStore.ContextOf(DataMode.Demo, b));

        var store = new RunnerJobStore();
        store.Set(keys[1] + "|k", []);
        await Assert.That(store.TryGet(keys[1] + "|k", out _)).IsTrue();
        await Assert.That(store.TryGet(keys[2] + "|k", out _)).IsFalse();
    }
}

public class RunnerPlanStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ciw-plans", Guid.NewGuid().ToString("N"));
    private static RunnerPlanStore Store(string dir) => new(Options.Create(new RunnerInsightsOptions { DataDirectory = dir }));
    private static ProviderPlanOptions Plan(double amount) => new() { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = amount, Confirmed = true, Note = "my plan" };

    [Test]
    public async Task Saved_allowances_survive_a_restart_and_reset_removes_them()
    {
        var dir = TempDir();
        Store(dir).Save("live|abc|", "github.acme", Plan(3000));
        var again = Store(dir);
        await Assert.That(again.Get("live|abc|")["github.acme"].AllowanceAmount).IsEqualTo(3000d);
        await Assert.That(again.Get("live|abc|")["github.acme"].Allowance).IsEqualTo(AllowanceKind.MonthlyIncluded);
        await Assert.That(again.Reset("live|abc|", "github.acme")).IsTrue();
        await Assert.That(Store(dir).Get("live|abc|")).IsEmpty();
    }

    [Test]
    public async Task Scopes_are_isolated_and_the_file_contains_no_token_or_repository_names()
    {
        var dir = TempDir(); var store = Store(dir);
        store.Save("live|tokenfingerprint1|secret-org/*", "avrea", Plan(100));
        store.Save("live|tokenfingerprint2|", "avrea", Plan(200));
        await Assert.That(store.Get("live|tokenfingerprint1|secret-org/*")["avrea"].AllowanceAmount).IsEqualTo(100d);
        await Assert.That(store.Get("live|tokenfingerprint2|")["avrea"].AllowanceAmount).IsEqualTo(200d);
        await Assert.That(store.Get("live|other|")).IsEmpty();
        var text = File.ReadAllText(store.FilePath);
        await Assert.That(text).DoesNotContain("tokenfingerprint").And.DoesNotContain("secret-org");
    }

    [Test]
    public async Task The_demo_scope_is_editable_but_never_written_to_disk()
    {
        var dir = TempDir(); var store = Store(dir);
        store.SeedDemo("demo", DemoRunnerPlans.Create());
        store.Save("demo", "blacksmith", Plan(1));
        await Assert.That(store.Get("demo").ContainsKey("blacksmith")).IsTrue();
        await Assert.That(File.Exists(store.FilePath)).IsFalse();
    }

    [Test]
    public async Task A_corrupt_file_is_ignored_instead_of_breaking_the_app()
    {
        var dir = TempDir(); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "runner-insights.json"), "{ not json");
        var store = Store(dir);
        await Assert.That(store.Get("live|x|")).IsEmpty();
        store.Save("live|x|", "tenki", Plan(5));                                           // and it can be written again
        await Assert.That(Store(dir).Get("live|x|")["tenki"].AllowanceAmount).IsEqualTo(5d);
    }
}
