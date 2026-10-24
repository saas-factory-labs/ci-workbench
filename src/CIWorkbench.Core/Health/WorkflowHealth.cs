using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Health;

/// <summary>
/// Contextual health of a workflow, derived only from loaded run history. It never replaces the primary
/// GitHub Actions state of the latest run; it explains whether a failure is actionable, historical or unproven.
/// </summary>
public enum HealthKind
{
    /// <summary>Latest verdict is a success and no earlier failure is in the loaded history (or it is older than the recovery window).</summary>
    Healthy,
    /// <summary>Latest verdict is a failure that no newer successful completed run supersedes.</summary>
    FailingNow,
    /// <summary>Latest verdict is a success and a failure precedes it within the recovery window.</summary>
    Recovered,
    /// <summary>No success/failure verdict exists in the loaded history, so nothing can be claimed.</summary>
    Unknown,
}

/// <param name="Kind">Failing now / recovered / healthy / unknown.</param>
/// <param name="Stale">Failing now, but the failing run is older than <see cref="WorkflowHealthAnalyzer.StaleAfter"/>.</param>
/// <param name="Recurring">A failure-series analysis exists and found at least one recurring series. False means "not established", not "one-off".</param>
/// <param name="Verdict">The newest run that reached a success/failure verdict within the scope.</param>
/// <param name="HasActiveRun">A queued or running run exists within the scope.</param>
/// <param name="ScopedToBranch">The branch whose runs were considered; null when every branch was considered.</param>
public sealed record WorkflowHealth(HealthKind Kind, bool Stale, bool Recurring, WorkflowRunInfo? Verdict, bool HasActiveRun, string? ScopedToBranch)
{
    /// <summary>Actionable now: failing and not stale.</summary>
    public bool NeedsAttention => Kind == HealthKind.FailingNow && !Stale;

    /// <summary>Lower number = more prominent in the attention view.</summary>
    public int Priority => Kind switch
    {
        HealthKind.FailingNow when Recurring && !Stale => 0,
        HealthKind.FailingNow when !Stale => 1,
        _ when HasActiveRun && Kind != HealthKind.Healthy => 2,
        HealthKind.FailingNow => 3,       // stale failure: historical
        HealthKind.Recovered => 3,
        HealthKind.Healthy => 5,
        _ => 6,
    };
}

public static class WorkflowHealthAnalyzer
{
    /// <summary>A failing verdict older than this is shown as stale rather than as a fresh incident.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>A success counts as "recovered" only if a failure occurred among this many verdicts before it.</summary>
    public const int RecoveryWindowVerdicts = 5;

    /// <summary>Runs ending in success or failure; cancelled, skipped, neutral and active runs say nothing about health.</summary>
    public static bool IsVerdict(RunState s) => s is RunState.Success or RunState.Failure;

    /// <param name="runs">Runs of ONE workflow, any order.</param>
    /// <param name="defaultBranch">When known and the workflow has runs on it, only those runs are considered.</param>
    /// <param name="hasRecurringSeries">Null when no failure analysis ran (unknown); otherwise whether a recurring series was found.</param>
    public static WorkflowHealth Assess(IEnumerable<WorkflowRunInfo> runs, string? defaultBranch, bool? hasRecurringSeries, DateTimeOffset now)
    {
        var all = runs.ToList();
        string? scope = null;
        if (!string.IsNullOrEmpty(defaultBranch) && all.Any(r => r.Branch == defaultBranch)) { scope = defaultBranch; all = all.Where(r => r.Branch == defaultBranch).ToList(); }

        var ordered = all.OrderByDescending(r => r.SortTime).ToList();
        var verdicts = ordered.Where(r => IsVerdict(r.State)).ToList();
        var active = ordered.Any(r => r.State.IsActive());
        if (verdicts.Count == 0) return new WorkflowHealth(HealthKind.Unknown, false, false, null, active, scope);

        var latest = verdicts[0];
        if (latest.State == RunState.Failure)
        {
            var stale = now - latest.SortTime > StaleAfter;
            return new WorkflowHealth(HealthKind.FailingNow, stale, hasRecurringSeries == true, latest, active, scope);
        }

        var recovered = verdicts.Skip(1).Take(RecoveryWindowVerdicts).Any(r => r.State == RunState.Failure);
        return new WorkflowHealth(recovered ? HealthKind.Recovered : HealthKind.Healthy, false, false, latest, active, scope);
    }
}

/// <summary>A KPI value with an optional change versus an earlier slice of the same loaded window.</summary>
public sealed record KpiTrend(double Delta, int RecentCount, int EarlierCount);

public sealed record SuccessRateKpi(double? Rate, int Succeeded, int Failed, int RunsInScope, KpiTrend? Trend);

public sealed record AttentionKpi(int FailingNow, int Stale, int Recovered, int Total);

public sealed record DurationKpi(TimeSpan? Average, int Samples, KpiTrend? Trend);

public static class Kpis
{
    /// <summary>Each half of the window needs this many data points before a comparison is shown.</summary>
    public const int MinPerPeriod = 10;

    /// <summary>
    /// Success rate = succeeded / (succeeded + failed) over the loaded runs. The trend compares the newer half of the
    /// verdict runs with the older half (percentage points) and exists only when both halves have <see cref="MinPerPeriod"/> runs.
    /// </summary>
    public static SuccessRateKpi SuccessRate(IReadOnlyList<WorkflowRunInfo> runs)
    {
        var v = runs.Where(r => WorkflowHealthAnalyzer.IsVerdict(r.State)).OrderByDescending(r => r.SortTime).ToList();
        var ok = v.Count(r => r.State == RunState.Success);
        double? rate = v.Count == 0 ? null : (double)ok / v.Count;
        KpiTrend? trend = null;
        var half = v.Count / 2;
        if (half >= MinPerPeriod)
        {
            var recent = v.Take(half).ToList(); var earlier = v.Skip(v.Count - half).ToList();
            double R(List<WorkflowRunInfo> l) => (double)l.Count(r => r.State == RunState.Success) / l.Count;
            trend = new KpiTrend((R(recent) - R(earlier)) * 100, recent.Count, earlier.Count);
        }
        return new SuccessRateKpi(rate, ok, v.Count - ok, runs.Count, trend);
    }

    /// <summary>Average duration of completed (success or failure) runs only; the trend is newer half vs older half.</summary>
    public static DurationKpi AverageDuration(IReadOnlyList<WorkflowRunInfo> runs)
    {
        var d = runs.Where(r => WorkflowHealthAnalyzer.IsVerdict(r.State) && r.Duration is { } x && x > TimeSpan.Zero)
            .OrderByDescending(r => r.SortTime).Select(r => r.Duration!.Value.TotalSeconds).ToList();
        if (d.Count == 0) return new DurationKpi(null, 0, null);
        KpiTrend? trend = null;
        var half = d.Count / 2;
        if (half >= MinPerPeriod) trend = new KpiTrend(d.Take(half).Average() - d.Skip(d.Count - half).Average(), half, half);
        return new DurationKpi(TimeSpan.FromSeconds(d.Average()), d.Count, trend);
    }

    public static AttentionKpi Attention(IEnumerable<WorkflowHealth> healths)
    {
        int now = 0, stale = 0, rec = 0, total = 0;
        foreach (var h in healths)
        {
            total++;
            if (h.Kind == HealthKind.FailingNow) { if (h.Stale) stale++; else now++; }
            else if (h.Kind == HealthKind.Recovered) rec++;
        }
        return new AttentionKpi(now, stale, rec, total);
    }
}
