using CIWorkbench.Core.Models;
using CIWorkbench.Core.Runners;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class RunnerInsightsTests
{
    private static readonly RunnerCatalog Catalog = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static RunnerIdentity Id(params string[] labels) => Catalog.Identify(labels);

    private static JobSample Job(long id, string[] labels, double minutes, RunState state = RunState.Success, int hoursAgo = 5, bool isPrivate = true, string repo = "app", string owner = "acme", int attempt = 1, bool started = true, bool completed = true)
    {
        var start = Now.AddHours(-hoursAgo);
        return new JobSample(new RepoRef(owner, repo), isPrivate, 1, 7, ".github/workflows/ci.yml", "CI", id, "build", attempt, labels, null, null, state,
            start.AddSeconds(-20), started ? start : null, completed ? start.AddMinutes(minutes) : null, "u");
    }

    private static UsageEstimate Estimate(RunnerInsightsOptions? o, params JobSample[] jobs) =>
        new RunnerUsageEstimator(Catalog, o ?? new()).Estimate(jobs, Now);

    // ------------------------------------------------------------ identification

    [Test]
    [Arguments("ubuntu-latest", "github", 1)]
    [Arguments("windows-2022", "github", 2)]
    [Arguments("macos-latest", "github", 10)]
    [Arguments("ubuntu-24.04-arm", "github", 1)]
    [Arguments("tenki-standard-small-2c-4g", "tenki", 2)]
    [Arguments("tenki-standard-large-8c-16g", "tenki", 8)]
    [Arguments("avrea-ubuntu-latest", "avrea", 1)]
    [Arguments("avrea-ubuntu-latest-4-vcpu", "avrea", 2)]
    [Arguments("bp-ubuntu-latest-8-vcpu", "buildpulse", 4)]
    [Arguments("celesto-cloud", "celesto", 1)]
    [Arguments("hetzner-arc", "hetzner-arc", 1)]
    [Arguments("blacksmith-4vcpu-ubuntu-2404", "blacksmith", 2)]
    [Arguments("ubuntu-latest-8-cores", "github-larger", 1)]
    public async Task Labels_resolve_to_one_provider_with_the_right_multiplier(string label, string provider, double multiplier)
    {
        var id = Id(label);
        await Assert.That(id.ProviderId).IsEqualTo(provider);
        await Assert.That(id.Multiplier).IsEqualTo(multiplier);
    }

    [Test]
    public async Task A_provider_prefix_wins_over_the_github_label_it_contains()
    {
        // The substring trap: "avrea-ubuntu-latest" contains "ubuntu-latest".
        await Assert.That(Id("avrea-ubuntu-latest").ProviderId).IsEqualTo("avrea");
        await Assert.That(Id("self-hosted", "tenki-standard-small-2c-4g").ProviderId).IsEqualTo("tenki");
    }

    [Test]
    public async Task Self_hosted_and_unknown_labels_are_reported_not_dropped()
    {
        await Assert.That(Id("self-hosted", "linux", "x64").ProviderId).IsEqualTo("self-hosted");
        var unknown = Id("my-weird-runner");
        await Assert.That(unknown.ProviderId).IsEqualTo("custom");
        await Assert.That(unknown.RunnerType).IsEqualTo("my-weird-runner");
        await Assert.That(Catalog.Identify([], "runner-7").ProviderId).IsEqualTo("custom");
    }

    [Test]
    public async Task Custom_rules_add_providers_without_code_changes()
    {
        var catalog = new RunnerCatalog([new CustomRunnerRule { Prefix = "acme-", ProviderId = "acme", Name = "Acme CI", Unit = UsageUnit.CoreMinutes, Multiplier = 4 }]);
        var id = catalog.Identify(["acme-big"]);
        await Assert.That(id.ProviderId).IsEqualTo("acme");
        await Assert.That(id.Multiplier).IsEqualTo(4);
        await Assert.That(catalog.Get("acme").Unit).IsEqualTo(UsageUnit.CoreMinutes);
    }

    // ------------------------------------------------------------ billing period

    [Test]
    public async Task Github_period_is_the_calendar_month_in_utc()
    {
        var p = BillingPeriod.For(new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.FromHours(-5)));   // already 1 Nov in UTC
        await Assert.That(p.Start).IsEqualTo(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(p.End).IsEqualTo(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task A_mid_month_cycle_rolls_back_before_its_anchor_and_clamps_day_31()
    {
        var before = BillingPeriod.For(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero), 15);
        await Assert.That(before.Start).IsEqualTo(new DateTimeOffset(2025, 12, 15, 0, 0, 0, TimeSpan.Zero));
        var clamped = BillingPeriod.For(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), 31);
        await Assert.That(clamped.Start).IsEqualTo(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(BillingPeriod.For(Now).Contains(Now.AddMinutes(-1))).IsTrue();
    }

    [Test]
    public async Task Jobs_before_the_period_start_are_excluded()
    {
        var e = Estimate(null, Job(1, ["ubuntu-latest"], 3, hoursAgo: 24 * 30), Job(2, ["ubuntu-latest"], 3));
        await Assert.That(e.Jobs.Count).IsEqualTo(1);
        await Assert.That(e.Excluded[ExclusionReason.OutsidePeriod]).IsEqualTo(1);
    }

    // ------------------------------------------------------------ normalization and rounding

    [Test]
    public async Task Github_rounds_each_job_up_to_a_minute_and_weights_by_os()
    {
        var e = Estimate(null, Job(1, ["ubuntu-latest"], 0.2), Job(2, ["windows-latest"], 2.1), Job(3, ["macos-latest"], 1));
        await Assert.That(e.Jobs.Select(j => j.RawMinutes).ToArray()).IsEquivalentTo(new[] { 1d, 3d, 1d });
        await Assert.That(e.Jobs.Sum(j => j.BilledUnits)).IsEqualTo(1 + 6 + 10d);
    }

    [Test]
    public async Task Tenki_counts_core_minutes_and_avrea_normalizes_to_two_vcpu()
    {
        var e = Estimate(null, Job(1, ["tenki-standard-small-2c-4g"], 100), Job(2, ["avrea-ubuntu-latest-4-vcpu"], 100), Job(3, ["avrea-ubuntu-latest"], 10));
        await Assert.That(e.Jobs[0].BilledUnits).IsEqualTo(200d);
        await Assert.That(e.Jobs[1].BilledUnits).IsEqualTo(200d);
        await Assert.That(e.Jobs[2].BilledUnits).IsEqualTo(10d);
    }

    [Test]
    public async Task Self_hosted_time_is_exact_not_rounded()
    {
        var e = Estimate(null, Job(1, ["hetzner-arc"], 0.5));
        await Assert.That(e.Jobs[0].RawMinutes).IsEqualTo(0.5);
    }

    [Test]
    public async Task Github_standard_runners_in_public_repositories_use_no_allowance()
    {
        var e = Estimate(null, Job(1, ["ubuntu-latest"], 10, isPrivate: false));
        await Assert.That(e.Jobs[0].RawMinutes).IsEqualTo(10d);
        await Assert.That(e.Jobs[0].BilledUnits).IsEqualTo(0d);
    }

    // ------------------------------------------------------------ what counts

    [Test]
    public async Task Pending_unstarted_skipped_and_duplicate_jobs_are_not_usage()
    {
        var e = Estimate(null,
            Job(1, ["ubuntu-latest"], 5, state: RunState.InProgress, completed: false),
            Job(2, ["ubuntu-latest"], 5, state: RunState.Queued, started: false, completed: false),
            Job(3, ["ubuntu-latest"], 5, state: RunState.Skipped),
            Job(4, ["ubuntu-latest"], 5), Job(4, ["ubuntu-latest"], 5));
        await Assert.That(e.Jobs.Count).IsEqualTo(1);
        await Assert.That(e.Excluded[ExclusionReason.NotCompleted]).IsEqualTo(2);
        await Assert.That(e.Excluded[ExclusionReason.Skipped]).IsEqualTo(1);
        await Assert.That(e.Excluded[ExclusionReason.Duplicate]).IsEqualTo(1);
    }

    [Test]
    public async Task Reruns_and_matrix_legs_count_once_per_job_id_and_queue_time_is_free()
    {
        // Attempt 1 and 2 of one run are different job ids and both ran; the matrix legs share a name but not an id.
        var e = Estimate(null, Job(10, ["ubuntu-latest"], 4, attempt: 1), Job(11, ["ubuntu-latest"], 4, attempt: 2), Job(12, ["ubuntu-latest"], 4), Job(13, ["ubuntu-latest"], 4));
        await Assert.That(e.Jobs.Count).IsEqualTo(4);
        // Queue time (created 20 s before start) does not change the executed minutes.
        await Assert.That(e.Jobs.All(j => j.RawMinutes == 4)).IsTrue();
    }

    [Test]
    public async Task The_same_job_listed_by_two_attempt_listings_is_counted_once()
    {
        var e = Estimate(null, Job(20, ["ubuntu-latest"], 6, attempt: 2), Job(20, ["ubuntu-latest"], 6, attempt: 2));
        await Assert.That(e.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Providers_use_their_own_billing_cycle()
    {
        var o = new RunnerInsightsOptions();
        o.Providers["tenki"] = new ProviderPlanOptions { CycleStartDay = 20 };       // Now is 24 Oct, so this period began 20 Oct
        var e = Estimate(o, Job(1, ["tenki-standard-small-2c-4g"], 3, hoursAgo: 24 * 6), Job(2, ["tenki-standard-small-2c-4g"], 3, hoursAgo: 24 * 2));
        await Assert.That(e.Jobs.Count).IsEqualTo(1);
    }

    // ------------------------------------------------------------ allowances

    private static RunnerProviderDef Def(string id) => Catalog.Get(id);
    private static ProviderPlanOptions Monthly(double amount, bool confirmed = true) => new() { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = amount, Confirmed = confirmed };

    [Test]
    public async Task An_unconfigured_allowance_never_invents_remaining_minutes()
    {
        var a = Allowances.Evaluate(Def("github"), null, 1500, true);
        await Assert.That(a.Kind).IsEqualTo(AllowanceKind.None);
        await Assert.That(a.Remaining).IsNull();
        await Assert.That(a.Explanation).IsEqualTo("Allowance unknown");
        var unknownAmount = Allowances.Evaluate(Def("github"), new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded }, 10, true);
        await Assert.That(unknownAmount.Remaining).IsNull();
    }

    [Test]
    public async Task A_monthly_allowance_gives_remaining_only_from_complete_data()
    {
        var plan = Monthly(2000);
        var half = Allowances.Evaluate(Def("github"), plan, 500, complete: true);
        await Assert.That(half.Remaining).IsEqualTo(1500d);
        await Assert.That(half.Fraction).IsEqualTo(0.25);
        var over = Allowances.Evaluate(Def("github"), plan, 2500, complete: true);
        await Assert.That(over.Remaining).IsEqualTo(0d);
        await Assert.That(over.Exhausted).IsTrue();

        var partial = Allowances.Evaluate(Def("github"), plan, 500, complete: false);
        await Assert.That(partial.Remaining).IsNull();                                    // incomplete history is a lower bound, never a basis for "left"
        await Assert.That(partial.AtLeast).IsTrue();
        await Assert.That(partial.Used).IsEqualTo(500d);
    }

    [Test]
    public async Task Official_usage_covers_the_whole_account_so_it_can_derive_remaining_even_when_collection_is_partial()
    {
        var a = Allowances.Evaluate(Def("github"), Monthly(2000), observedUnits: 28, complete: false, officialUnits: 784);
        await Assert.That(a.Remaining).IsEqualTo(1216d);
        await Assert.That(a.Basis).IsEqualTo("official usage");
    }

    [Test]
    public async Task Unlimited_has_no_remaining_and_credits_need_a_rate()
    {
        await Assert.That(Allowances.Evaluate(Def("blacksmith"), new ProviderPlanOptions { Allowance = AllowanceKind.Unlimited }, 99, true).Remaining).IsNull();
        var monthly = new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 10, AllowanceUnit = UsageUnit.UsdCredits };
        await Assert.That(Allowances.Evaluate(Def("tenki"), monthly, 1000, true).Remaining).IsNull();   // core-minutes cannot be compared with USD without a rate
        monthly.RatePerUnit = 0.002;
        await Assert.That(Allowances.Evaluate(Def("tenki"), monthly, 1000, true).Remaining).IsEqualTo(8d);
    }

    [Test]
    public async Task One_time_and_prepaid_balances_are_derived_from_a_dated_balance_and_complete_usage_since()
    {
        var period = BillingPeriod.For(Now);
        var asOf = period.Start.AddDays(3);
        var oneTime = new ProviderPlanOptions { Allowance = AllowanceKind.OneTimeCredit, AllowanceUnit = UsageUnit.UsdCredits, RatePerUnit = 0.002, BalanceAmount = 10, BalanceAsOf = asOf };
        var ok = Allowances.Evaluate(Def("tenki"), oneTime, observedUnits: 5000, complete: true, observedSinceBalance: 1000, periodStart: period.Start);
        await Assert.That(ok.Remaining).IsEqualTo(8d);                                     // $10 balance minus 1,000 core-min x $0.002 since it was read
        var partial = Allowances.Evaluate(Def("tenki"), oneTime, 5000, complete: false, observedSinceBalance: 1000, periodStart: period.Start);
        await Assert.That(partial.Remaining).IsNull();
        var stale = oneTime.BalanceAsOf = period.Start.AddDays(-2);
        await Assert.That(Allowances.Evaluate(Def("tenki"), oneTime, 5000, true, null, 1000, period.Start).Remaining).IsNull();   // balance read in a previous period

        var noBalance = new ProviderPlanOptions { Allowance = AllowanceKind.Prepaid, AllowanceUnit = UsageUnit.Minutes };
        await Assert.That(Allowances.Evaluate(Def("celesto"), noBalance, 5, true).Remaining).IsNull();
        await Assert.That(Allowances.Evaluate(Def("celesto"), noBalance, 5, true).Explanation).Contains("Prepaid");
    }

    [Test]
    public async Task Self_managed_runners_have_no_allowance_and_no_cost_claim()
    {
        var a = Allowances.Evaluate(Def("hetzner-arc"), null, 400, true);
        await Assert.That(a.Remaining).IsNull();
        await Assert.That(Def("hetzner-arc").CostTracked).IsFalse();
    }

    [Test]
    public async Task Plans_are_validated_before_they_are_saved()
    {
        var tenki = Def("tenki");
        var now = Now;
        await Assert.That(PlanValidator.Validate(tenki, Monthly(0), now)).IsNotEmpty();
        await Assert.That(PlanValidator.Validate(tenki, new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 10, AllowanceUnit = UsageUnit.UsdCredits }, now)).IsNotEmpty();   // USD without a rate
        await Assert.That(PlanValidator.Validate(tenki, new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 10, AllowanceUnit = UsageUnit.UsdCredits, RatePerUnit = 0.002 }, now)).IsEmpty();
        await Assert.That(PlanValidator.Validate(tenki, new ProviderPlanOptions { Allowance = AllowanceKind.Prepaid, BalanceAmount = 5 }, now)).IsNotEmpty();   // balance without a date
        await Assert.That(PlanValidator.Validate(tenki, new ProviderPlanOptions { Allowance = AllowanceKind.Prepaid, BalanceAmount = 5, BalanceAsOf = now.AddDays(2) }, now)).IsNotEmpty();
        await Assert.That(PlanValidator.Validate(tenki, new ProviderPlanOptions { Allowance = AllowanceKind.None, CycleStartDay = 31 }, now)).IsNotEmpty();
        await Assert.That(PlanValidator.Validate(Def("avrea"), new ProviderPlanOptions { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 100, AllowanceUnit = UsageUnit.CoreMinutes }, now)).IsNotEmpty();   // wrong unit
    }

    [Test]
    public async Task Suggestions_are_labelled_and_never_applied_by_the_catalog()
    {
        await Assert.That(PlanSuggestions.For("github").Select(s => s.Amount).ToArray()).IsEquivalentTo(new[] { 2000d, 3000d, 50000d });
        await Assert.That(PlanSuggestions.For("tenki")).IsEmpty();            // Tenki's credit policy changes: nothing is suggested, let alone assumed
        await Assert.That(PlanSuggestions.For("hetzner-arc")).IsEmpty();
        await Assert.That(PlanSuggestions.For("celesto")).IsEmpty();
        await Assert.That(new RunnerInsightsOptions().PlanOf("github", "acme")).IsNull();
    }

    [Test]
    public async Task Github_plans_are_per_account_and_the_account_entry_wins_over_a_general_one()
    {
        var o = new RunnerInsightsOptions();
        o.Providers["github"] = Monthly(2000);
        o.Providers["github.acme"] = Monthly(3000);
        await Assert.That(o.PlanOf("github", "acme")!.AllowanceAmount).IsEqualTo(3000d);
        await Assert.That(o.PlanOf("github", "other")!.AllowanceAmount).IsEqualTo(2000d);
    }

    // ------------------------------------------------------------ billing report weighting

    private static BillingLine L(string sku, double qty, double net = 0) => new(sku, "Minutes", qty, 0.006, qty * 0.006, qty * 0.006 - net, net);
    private static BillingUsage Report(params BillingLine[] lines) => new(BillingAccess.Available, lines, null, 2026, 10, Now);

    [Test]
    public async Task Billing_weights_standard_skus_counts_gross_usage_and_ignores_larger_runners()
    {
        var b = Report(L("Actions Linux", 1000), L("actions_windows", 100), L("Actions macOS", 10), L("Actions Linux 4-core", 500, net: 3));
        await Assert.That(b.StandardWeightedMinutes()).IsEqualTo(1000 + 200 + 100d);
        await Assert.That(b.StandardMinutesCharged()).IsFalse();                          // the only charge is on a larger runner, which never uses included minutes
    }

    [Test]
    public async Task Billed_minutes_ignore_storage_and_other_non_minute_lines()
    {
        var b = Report(L("Actions Linux", 708), new BillingLine("Actions storage", "GigabyteHours", 75.8, 0.000336, 0.0255, 0.0255, 0));
        await Assert.That(b.BilledMinutes()).IsEqualTo(708d);
        await Assert.That(b.StandardWeightedMinutes()).IsEqualTo(708d);
    }

    [Test]
    public async Task A_charge_on_a_standard_sku_means_exhausted_and_a_zero_dollar_bill_proves_nothing()
    {
        await Assert.That(Report(L("Actions Linux", 3000, net: 4)).StandardMinutesCharged()).IsTrue();
        await Assert.That(Report(L("Actions Linux", 100)).StandardMinutesCharged()).IsFalse();
        await Assert.That(new BillingUsage(BillingAccess.PermissionMissing, [], "x", 2026, 10, null).StandardWeightedMinutes()).IsNull();
    }

    // ------------------------------------------------------------ aggregation

    private static RunnerUsageReport Build(RunnerInsightsOptions o, UsageEstimate est, UsageCoverage cov, Dictionary<string, UsageCoverage>? byOwner = null, Dictionary<string, BillingUsage>? billing = null) =>
        new RunnerUsageAggregator(Catalog, o).Build(est, cov, byOwner ?? [], billing ?? [], Now);

    private static readonly UsageCoverage Done = new(2, 2, 10, 10, false, 0);

    [Test]
    public async Task Units_are_never_mixed_across_providers_and_pools_are_per_account()
    {
        var o = new RunnerInsightsOptions();
        var est = Estimate(o, Job(1, ["ubuntu-latest"], 10), Job(2, ["ubuntu-latest"], 5, owner: "other"), Job(3, ["tenki-standard-small-2c-4g"], 10), Job(4, ["avrea-ubuntu-latest"], 10));
        var report = Build(o, est, Done);
        var github = report.Providers.Single(p => p.Provider.Id == "github");
        await Assert.That(github.Pools.Select(p => p.Pool).ToArray()).IsEquivalentTo(new[] { "acme", "other" });
        await Assert.That(github.Unit).IsEqualTo(UsageUnit.Minutes);
        await Assert.That(report.Providers.Single(p => p.Provider.Id == "tenki").Unit).IsEqualTo(UsageUnit.CoreMinutes);
        await Assert.That(report.Providers.Single(p => p.Provider.Id == "avrea").Unit).IsEqualTo(UsageUnit.NormalizedMinutes);
    }

    [Test]
    public async Task Billing_and_observed_usage_stay_separate_and_each_account_has_its_own_coverage_and_allowance()
    {
        var o = new RunnerInsightsOptions();
        o.Providers["github.acme"] = Monthly(2000);
        var est = Estimate(o, Job(1, ["ubuntu-latest"], 10), Job(2, ["ubuntu-latest"], 5, owner: "other"));
        var cov = new Dictionary<string, UsageCoverage> { ["acme"] = new(1, 0, 100, 3, false, 0), ["other"] = new(1, 1, 4, 4, false, 0) };
        var billing = new Dictionary<string, BillingUsage>
        {
            ["acme"] = Report(L("Actions Linux", 784)),
            ["other"] = new(BillingAccess.PermissionMissing, [], "needs Administration: read", 2026, 10, Now),
        };
        var github = Build(o, est, new UsageCoverage(2, 1, 104, 7, false, 0), cov, billing).Providers.Single();
        var acme = github.Pools.Single(p => p.Pool == "acme"); var other = github.Pools.Single(p => p.Pool == "other");
        await Assert.That(acme.Observed).IsEqualTo(10d);                                  // observed jobs: 10 minutes
        await Assert.That(acme.VerifiedUsed).IsEqualTo(784d);                             // official billing: 784 minutes. Two different measurements, kept apart
        await Assert.That(acme.Coverage.Overall).IsEqualTo(Coverage.Partial);
        await Assert.That(acme.Allowance.Remaining).IsEqualTo(1216d);                     // from the account-wide official figure, not from the partial jobs
        await Assert.That(other.BillingVerified).IsFalse();
        await Assert.That(other.Coverage.Overall).IsEqualTo(Coverage.Complete);
        await Assert.That(other.Allowance.Remaining).IsNull();                            // no plan stated for this account
    }

    [Test]
    public async Task Partial_collection_never_yields_remaining_for_providers_without_official_usage()
    {
        var o = new RunnerInsightsOptions();
        o.Providers["avrea"] = Monthly(3000);
        var est = Estimate(o, Job(1, ["avrea-ubuntu-latest"], 100));
        var partial = Build(o, est, new UsageCoverage(1, 0, 50, 10, false, 0)).Providers.Single();
        await Assert.That(partial.Pools.Single().Allowance.Remaining).IsNull();
        await Assert.That(partial.Pools.Single().Allowance.AtLeast).IsTrue();
        var complete = Build(o, est, new UsageCoverage(1, 1, 10, 10, false, 0)).Providers.Single();
        await Assert.That(complete.Pools.Single().Allowance.Remaining).IsEqualTo(2900d);
    }

    [Test]
    public async Task Verified_billing_flags_exhaustion_from_a_charge_on_standard_minutes()
    {
        var o = new RunnerInsightsOptions();
        var est = Estimate(o, Job(1, ["ubuntu-latest"], 10));
        var billing = new Dictionary<string, BillingUsage> { ["acme"] = Report(L("Actions Linux", 2100, net: 0.6)) };
        var pool = Build(o, est, Done, billing: billing).Providers.Single().Pools.Single();
        await Assert.That(pool.FreeAllowanceExhaustedVerified).IsTrue();
    }

    [Test]
    public async Task Coverage_is_partial_when_runs_are_unmeasured_or_the_window_is_truncated()
    {
        await Assert.That(new UsageCoverage(3, 3, 20, 20, false, 0).Overall).IsEqualTo(Coverage.Complete);
        await Assert.That(new UsageCoverage(3, 3, 20, 12, false, 0).Overall).IsEqualTo(Coverage.Partial);
        await Assert.That(new UsageCoverage(3, 3, 20, 12, false, 0).RunsMissing).IsEqualTo(8);
        await Assert.That(new UsageCoverage(3, 2, 20, 20, false, 0).Overall).IsEqualTo(Coverage.Partial);
        await Assert.That(new UsageCoverage(3, 3, 20, 20, true, 0).Overall).IsEqualTo(Coverage.Partial);
        await Assert.That(new UsageCoverage(3, 3, 20, 20, false, 1).Overall).IsEqualTo(Coverage.Partial);
    }

    // ------------------------------------------------------------ runs-on resolution (shapes used by SaaS-Factory)

    private static string Wf(string runsOn, string extra = "") => $"name: t\non: push\njobs:\n  build:\n    runs-on: {runsOn}\n{extra}    steps:\n      - run: echo\n";

    private static ConfiguredRunner[] Resolve(string yaml, Dictionary<string, string>? vars) => RunsOnResolver.Resolve("w.yml", yaml, vars).Runners.ToArray();

    [Test]
    public async Task Static_labels_arrays_and_group_objects_are_configured_runners()
    {
        await Assert.That(Resolve(Wf("ubuntu-latest"), null)[0].Labels.Single()).IsEqualTo("ubuntu-latest");
        var arr = Resolve(Wf("[self-hosted, linux]"), null)[0];
        await Assert.That(arr.Labels.ToArray()).IsEquivalentTo(new[] { "self-hosted", "linux" });
        var grp = Resolve("jobs:\n  b:\n    runs-on:\n      group: big\n      labels: [ubuntu-latest-8-cores]\n    steps: []\n", null)[0];
        await Assert.That(grp.Role).IsEqualTo(RunnerRole.Configured);
        await Assert.That(grp.Note).Contains("big");
    }

    [Test]
    public async Task A_variable_with_a_default_reports_the_fallback_as_potential_only()
    {
        var yaml = Wf("${{ fromJSON(vars.CI_RUNNER_DEFAULT || '[\"tenki-standard-small-2c-4g\"]') }}");
        var set = Resolve(yaml, new() { ["CI_RUNNER_DEFAULT"] = "[\"avrea-ubuntu-latest\"]" });
        await Assert.That(set.Single(r => r.Role == RunnerRole.Configured).Labels.Single()).IsEqualTo("avrea-ubuntu-latest");
        await Assert.That(set.Single(r => r.Role == RunnerRole.Fallback).Labels.Single()).IsEqualTo("tenki-standard-small-2c-4g");
    }

    [Test]
    public async Task An_unset_variable_makes_its_default_the_configured_runner()
    {
        var r = Resolve(Wf("${{ fromJSON(vars.CI_RUNNER_HEAVY || '[\"hetzner-arc\"]') }}"), new Dictionary<string, string>());
        await Assert.That(r.Length).IsEqualTo(1);
        await Assert.That(r[0].Role).IsEqualTo(RunnerRole.Configured);
        await Assert.That(r[0].Labels.Single()).IsEqualTo("hetzner-arc");
    }

    [Test]
    public async Task An_unreadable_variable_is_unknown_and_its_default_is_only_a_fallback()
    {
        var r = Resolve(Wf("${{ fromJSON(vars.CI_RUNNER_FAST || '[\"avrea-ubuntu-latest-4-vcpu\"]') }}"), null);
        await Assert.That(r.Count(x => x.Role == RunnerRole.Unknown)).IsEqualTo(1);
        await Assert.That(r.Single(x => x.Role == RunnerRole.Fallback).Labels.Single()).IsEqualTo("avrea-ubuntu-latest-4-vcpu");
        await Assert.That(r.Any(x => x.Role == RunnerRole.Configured)).IsFalse();
    }

    [Test]
    public async Task Literal_matrices_expand_and_computed_ones_are_unknown()
    {
        var literal = Resolve(Wf("${{ matrix.os }}", "    strategy:\n      matrix:\n        os: [ubuntu-latest, windows-latest]\n"), null);
        await Assert.That(literal.Select(r => r.Labels.Single()).ToArray()).IsEquivalentTo(new[] { "ubuntu-latest", "windows-latest" });
        var computed = Resolve(Wf("${{ matrix.os }}", "    strategy:\n      matrix: ${{ fromJSON(needs.x.outputs.m) }}\n"), null);
        await Assert.That(computed.Single().Role).IsEqualTo(RunnerRole.Unknown);
    }

    [Test]
    public async Task Inputs_and_ternaries_are_dynamic_with_quoted_literals_as_candidates()
    {
        var dyn = Resolve(Wf("${{ fromJSON(inputs.runner_json) }}"), null);
        await Assert.That(dyn.Single().Role).IsEqualTo(RunnerRole.Unknown);
        var ternary = Resolve(Wf("${{ github.ref == 'refs/heads/main' && 'hetzner-arc' || 'ubuntu-latest' }}"), null);
        await Assert.That(ternary.Count(r => r.Role == RunnerRole.Configured)).IsEqualTo(0);
        await Assert.That(ternary.Count(r => r.Role == RunnerRole.Fallback && r.Labels[0] == "ubuntu-latest")).IsEqualTo(1);
    }

    [Test]
    public async Task Local_reusable_workflows_are_followed_and_remote_ones_are_unknown()
    {
        var cfg = RunsOnResolver.Resolve("c.yml", "on: push\njobs:\n  a:\n    uses: ./.github/workflows/build.yml\n  b:\n    uses: org/repo/.github/workflows/x.yml@v1\n", null);
        await Assert.That(cfg.LocalReusable["a"]).IsEqualTo(".github/workflows/build.yml");
        await Assert.That(cfg.Runners.Single().Role).IsEqualTo(RunnerRole.Unknown);
    }

    [Test]
    public async Task Broken_yaml_never_throws()
    {
        await Assert.That(RunsOnResolver.Resolve("x.yml", "jobs: [unclosed", null).ParseFailed).IsTrue();
        await Assert.That(RunsOnResolver.Resolve("x.yml", null, null).ParseFailed).IsTrue();
    }
}
