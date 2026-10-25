using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Runners;

public sealed record UsageEstimate(IReadOnlyList<JobUsage> Jobs, IReadOnlyDictionary<ExclusionReason, int> Excluded);

/// <summary>
/// Turns job history into provider units. Only finished jobs that actually started are counted, each job id once
/// (reruns and matrix legs are distinct job ids; a job returned twice, e.g. by two attempt listings, is counted once).
/// Time is <c>started_at → completed_at</c>: queue time is never billed and pending jobs are never usage.
/// </summary>
public sealed class RunnerUsageEstimator(RunnerCatalog catalog, RunnerInsightsOptions options)
{
    public UsageEstimate Estimate(IEnumerable<JobSample> samples, DateTimeOffset now)
    {
        var counted = new List<JobUsage>();
        var excluded = new Dictionary<ExclusionReason, int>();
        var seen = new HashSet<(RepoRef, long)>();
        void Skip(ExclusionReason r) => excluded[r] = excluded.GetValueOrDefault(r) + 1;

        foreach (var j in samples)
        {
            if (!seen.Add((j.Repo, j.JobId))) { Skip(ExclusionReason.Duplicate); continue; }
            if (!j.State.IsCompleted() || j.CompletedAt is null) { Skip(ExclusionReason.NotCompleted); continue; }
            if (j.State == RunState.Skipped) { Skip(ExclusionReason.Skipped); continue; }
            if (j.StartedAt is not { } started) { Skip(ExclusionReason.NotStarted); continue; }
            var elapsed = j.CompletedAt.Value - started;
            if (elapsed < TimeSpan.Zero) { Skip(ExclusionReason.InvalidDuration); continue; }

            var id = catalog.Identify(j.Labels, j.RunnerName);
            if (!BillingPeriod.For(now, options.CycleDayOf(id.ProviderId)).Contains(started)) { Skip(ExclusionReason.OutsidePeriod); continue; }

            var def = catalog.Get(id.ProviderId);
            var minutes = def.Rounding == RoundingRule.PerJobCeilMinute ? Math.Max(1d, Math.Ceiling(elapsed.TotalMinutes)) : elapsed.TotalMinutes;
            // GitHub's standard runners are free in public repositories: the time is real, but it draws nothing from any allowance.
            var units = id.ProviderId == RunnerCatalog.GitHub && !j.RepoIsPrivate ? 0 : minutes * id.Multiplier;
            counted.Add(new JobUsage(j, id, minutes, units));
        }
        return new UsageEstimate(counted, excluded);
    }
}

/// <summary>
/// Result of comparing usage with a user-stated allowance. <see cref="Remaining"/> is null whenever it cannot honestly be derived:
/// no plan, unknown balance, or <b>incomplete collection</b> (then <see cref="Used"/> is only a lower bound, <see cref="AtLeast"/>).
/// </summary>
/// <param name="Basis">What <see cref="Used"/> was derived from: official billing usage or observed workflow jobs.</param>
public sealed record AllowanceStatus(
    AllowanceKind Kind, double? Amount, UsageUnit? Unit, bool Confirmed, double? Used, double? Remaining, double? Fraction, bool Exhausted, string Explanation,
    bool AtLeast = false, string? Basis = null)
{
    public bool HasRemaining => Remaining is not null;
}

public static class Allowances
{
    /// <param name="observedUnits">Units from measured jobs (a lower bound while <paramref name="complete"/> is false).</param>
    /// <param name="complete">Collection covered the whole period for this pool.</param>
    /// <param name="officialUnits">Units from the provider's own billing report, which covers the whole account; null when unavailable.</param>
    /// <param name="observedSinceBalance">Observed units that started at or after <see cref="ProviderPlanOptions.BalanceAsOf"/>.</param>
    public static AllowanceStatus Evaluate(RunnerProviderDef provider, ProviderPlanOptions? plan, double observedUnits, bool complete, double? officialUnits = null, double observedSinceBalance = 0, DateTimeOffset? periodStart = null)
    {
        if (plan is null || plan.Allowance == AllowanceKind.None)
            return new(AllowanceKind.None, null, null, false, null, null, null, false,
                provider.CostTracked ? "Allowance unknown" : "No free allowance applies to self-managed runners.");
        if (plan.Allowance == AllowanceKind.Unlimited)
            return new(AllowanceKind.Unlimited, null, null, plan.Confirmed, null, null, null, false, "No cap configured: usage only.");

        var unit = plan.AllowanceUnit ?? provider.Unit;
        double? Convert(double units) => unit == provider.Unit ? units : unit == UsageUnit.UsdCredits && plan.RatePerUnit is { } r && r > 0 ? units * r : null;
        var incomplete = $"Remaining unknown: collection of this period is incomplete, so usage is shown as \"at least\".";

        if (plan.Allowance == AllowanceKind.MonthlyIncluded)
        {
            if (plan.AllowanceAmount is not { } amount || amount <= 0) return new(plan.Allowance, null, unit, plan.Confirmed, null, null, null, false, "Allowance type is set but its amount is not.");
            var basisUnits = officialUnits ?? observedUnits; var reliable = officialUnits is not null || complete;
            if (Convert(basisUnits) is not { } used) return new(plan.Allowance, amount, unit, plan.Confirmed, null, null, null, false, $"Usage is measured in {provider.Unit.Label()}; set a rate to compare it with {unit.Label()}.");
            var basis = officialUnits is not null ? "official usage" : "observed jobs";
            return reliable
                ? new(plan.Allowance, amount, unit, plan.Confirmed, used, Math.Max(0, amount - used), Math.Min(1, used / amount), used >= amount,
                    plan.Confirmed ? $"Estimated from {basis} and your confirmed allowance." : $"Estimated from {basis} and an allowance you have not confirmed.", false, basis)
                : new(plan.Allowance, amount, unit, plan.Confirmed, used, null, Math.Min(1, used / amount), used >= amount, incomplete, true, basis);
        }

        // One-time credits and prepaid balances: only a balance the user read at a known time lets remaining be derived.
        if (plan.BalanceAmount is not { } balance || plan.BalanceAsOf is not { } asOf)
            return new(plan.Allowance, plan.AllowanceAmount, unit, plan.Confirmed, Convert(observedUnits), null, null, false,
                plan.Allowance == AllowanceKind.Prepaid ? "Prepaid balance unknown: enter it with the date you read it." : "One-time credit: enter the current balance with the date you read it.", !complete, "observed jobs");
        if (periodStart is { } ps && asOf < ps)
            return new(plan.Allowance, balance, unit, plan.Confirmed, null, null, null, false, "The balance was read before this billing period: enter a fresh one.");
        if (Convert(observedSinceBalance) is not { } usedSince)
            return new(plan.Allowance, balance, unit, plan.Confirmed, null, null, null, false, $"Set a rate to compare {provider.Unit.Label()} with {unit.Label()}.");
        return complete
            ? new(plan.Allowance, balance, unit, plan.Confirmed, usedSince, Math.Max(0, balance - usedSince), balance > 0 ? Math.Min(1, usedSince / balance) : 1, usedSince >= balance,
                $"Estimated: balance read {asOf:d MMM HH:mm} minus observed usage since.", false, "observed jobs")
            : new(plan.Allowance, balance, unit, plan.Confirmed, usedSince, null, null, false, incomplete, true, "observed jobs");
    }
}

public sealed record RunnerTypeUsage(string RunnerType, int Jobs, double RawMinutes, double Units);
public sealed record RepoUsage(RepoRef Repo, int Jobs, double Units);
public sealed record WorkflowUsage(RepoRef Repo, long WorkflowId, string WorkflowPath, string WorkflowName, string ProviderId, string RunnerType, int Jobs, double Units, long LatestRunId);

/// <summary>
/// One shared quota: a GitHub account (organisation or user) or, for every other provider, the single provider account.
/// <see cref="Observed"/> is always derived from job history (never "verified"); <see cref="Billing"/> is GitHub's own report when readable.
/// </summary>
/// <param name="VerifiedUsed">Official, multiplier-weighted standard minutes of the whole account, when billing is readable.</param>
public sealed record PoolUsage(string Pool, double Observed, int Jobs, AllowanceStatus Allowance, BillingUsage? Billing, UsageCoverage Coverage, double? VerifiedUsed = null)
{
    public bool BillingVerified => Billing is { IsAvailable: true };
    /// <summary>GitHub charged for standard minutes: the included allowance is used up (verified), independent of any configured quota.</summary>
    public bool FreeAllowanceExhaustedVerified => Billing?.StandardMinutesCharged() == true;
}

public sealed record ProviderUsage(
    RunnerProviderDef Provider, BillingPeriod Period, double Observed, int Jobs, IReadOnlyList<PoolUsage> Pools,
    IReadOnlyList<RunnerTypeUsage> RunnerTypes, IReadOnlyList<RepoUsage> Repos, IReadOnlyList<WorkflowUsage> Workflows)
{
    public UsageUnit Unit => Provider.Unit;
    public bool Complete => Pools.Count > 0 && Pools.All(p => p.Coverage.Overall == Coverage.Complete);
}

/// <param name="RepositoriesMeasured">Repositories whose period runs were all measured, out of those in scope.</param>
/// <param name="RunsEligible">Finished runs of the period that exist (from the period listing).</param>
/// <param name="RunWindowTruncated">The period listing itself is incomplete (GitHub caps it), so more runs exist than were listed.</param>
public sealed record UsageCoverage(int RepositoriesInScope, int RepositoriesMeasured, int RunsEligible, int RunsMeasured, bool RunWindowTruncated, int RepositoriesFailed)
{
    public Coverage Overall => RepositoriesMeasured < RepositoriesInScope || RunsMeasured < RunsEligible || RunWindowTruncated || RepositoriesFailed > 0 ? Coverage.Partial : Coverage.Complete;
    public int RunsMissing => Math.Max(0, RunsEligible - RunsMeasured);
    public static readonly UsageCoverage None = new(0, 0, 0, 0, false, 0);
}

public sealed record RunnerUsageReport(
    IReadOnlyList<ProviderUsage> Providers, UsageCoverage Coverage, DateTimeOffset UpdatedAt, IReadOnlyDictionary<ExclusionReason, int> Excluded,
    IReadOnlyDictionary<string, BillingUsage> BillingByOwner)
{
    public static RunnerUsageReport Empty(DateTimeOffset now) => new([], UsageCoverage.None, now, new Dictionary<ExclusionReason, int>(), new Dictionary<string, BillingUsage>());
}

public sealed class RunnerUsageAggregator(RunnerCatalog catalog, RunnerInsightsOptions options)
{
    public RunnerUsageReport Build(UsageEstimate estimate, UsageCoverage coverage, IReadOnlyDictionary<string, UsageCoverage> coverageByOwner,
        IReadOnlyDictionary<string, BillingUsage> billingByOwner, DateTimeOffset now)
    {
        var providers = new List<ProviderUsage>();
        var byProvider = estimate.Jobs.GroupBy(j => j.Identity.ProviderId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // GitHub appears whenever any of its billing data was read, even before a single job has been measured.
        if (billingByOwner.Values.Any(b => b.IsAvailable) && !byProvider.ContainsKey(RunnerCatalog.GitHub)) byProvider[RunnerCatalog.GitHub] = [];

        foreach (var (id, jobs) in byProvider)
        {
            var def = catalog.Get(id);
            var period = BillingPeriod.For(now, options.CycleDayOf(id));
            var pools = id == RunnerCatalog.GitHub
                ? jobs.Select(j => j.Job.Repo.Owner).Concat(billingByOwner.Where(b => b.Value.IsAvailable).Select(b => b.Key)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
                    .Select(owner =>
                    {
                        var mine = jobs.Where(j => j.Job.Repo.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)).ToList();
                        var observed = mine.Sum(j => j.BilledUnits);
                        var cov = coverageByOwner.GetValueOrDefault(owner) ?? coverage;
                        var billing = billingByOwner.GetValueOrDefault(owner);
                        var official = billing?.StandardWeightedMinutes();
                        return new PoolUsage(owner, observed, mine.Count, Allowances.Evaluate(def, options.PlanOf(id, owner), observed, cov.Overall == Coverage.Complete, official, 0, period.Start), billing, cov, official);
                    }).ToList()
                : [Account(def, id, jobs, coverage, period)];

            providers.Add(new ProviderUsage(def, period, jobs.Sum(j => j.BilledUnits), jobs.Count, pools,
                jobs.GroupBy(j => j.Identity.RunnerType).Select(g => new RunnerTypeUsage(g.Key, g.Count(), g.Sum(j => j.RawMinutes), g.Sum(j => j.BilledUnits))).OrderByDescending(r => r.Units).ThenBy(r => r.RunnerType, StringComparer.Ordinal).ToList(),
                jobs.GroupBy(j => j.Job.Repo).Select(g => new RepoUsage(g.Key, g.Count(), g.Sum(j => j.BilledUnits))).OrderByDescending(r => r.Units).ThenBy(r => r.Repo.FullName, StringComparer.OrdinalIgnoreCase).ToList(),
                jobs.GroupBy(j => (j.Job.Repo, j.Job.WorkflowId, j.Identity.RunnerType)).Select(g => new WorkflowUsage(g.Key.Repo, g.Key.WorkflowId, g.First().Job.WorkflowPath, g.First().Job.WorkflowName, id, g.Key.RunnerType, g.Count(), g.Sum(j => j.BilledUnits), g.Max(j => j.Job.RunId))).ToList()));
        }
        // Stable order: known providers first by usage, then the catch-all buckets.
        var ordered = providers.OrderBy(p => p.Provider.Kind == RunnerProviderKind.Unknown ? 1 : 0).ThenByDescending(p => p.Jobs).ThenBy(p => p.Provider.Name, StringComparer.Ordinal).ToList();
        return new RunnerUsageReport(ordered, coverage, now, estimate.Excluded, billingByOwner);
    }

    private PoolUsage Account(RunnerProviderDef def, string id, List<JobUsage> jobs, UsageCoverage coverage, BillingPeriod period)
    {
        var plan = options.PlanOf(id);
        var observed = jobs.Sum(j => j.BilledUnits);
        var since = plan?.BalanceAsOf is { } at ? jobs.Where(j => j.Job.StartedAt >= at).Sum(j => j.BilledUnits) : 0;
        return new PoolUsage("account", observed, jobs.Count, Allowances.Evaluate(def, plan, observed, coverage.Overall == Coverage.Complete, null, since, period.Start), null, coverage);
    }
}
