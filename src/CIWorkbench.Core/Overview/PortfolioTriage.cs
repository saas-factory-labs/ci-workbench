using CIWorkbench.Core.Health;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Overview;

/// <summary>Everything the global dashboard knows about one repository: its workflows and one bounded window of runs. No jobs, no logs.</summary>
public sealed record RepoSnapshot(RepositoryInfo Repo, IReadOnlyList<WorkflowInfo> Workflows, IReadOnlyList<WorkflowRunInfo> Runs, DateTimeOffset LoadedAt);

/// <summary>Optional narrowing of the dashboard. A branch or event keeps only matching runs; workflows left without any run are omitted.</summary>
public sealed record TriageScope(string? Branch = null, string? Event = null)
{
    public static readonly TriageScope All = new();
    public bool IsNarrowed => !string.IsNullOrEmpty(Branch) || !string.IsNullOrEmpty(Event);
}

/// <summary>Quick filters of the "Needs attention" table. They never mix "not loaded" into "healthy".</summary>
public enum TriageFilter
{
    /// <summary>Every workflow whose newest verdict is a failure (fresh first, then stale).</summary>
    NeedsAttention,
    /// <summary>Fresh failures only.</summary>
    FailingNow,
    /// <summary>Fresh failures that repeated: <see cref="WorkflowTriage.FailureStreak"/> of two or more.</summary>
    Recurring,
    Recovered,
    /// <summary>A run is queued or in progress.</summary>
    Running,
    /// <summary>No success or failure verdict in the loaded runs.</summary>
    Unknown,
}

/// <summary>
/// One workflow of one repository, assessed with the same <see cref="WorkflowHealthAnalyzer"/> (default branch, 14-day staleness) as the workspace,
/// so a repository's count here matches its Workflow Groups page when both use the same run window.
/// </summary>
public sealed record WorkflowTriage(RepoRef Repo, WorkflowInfo Workflow, WorkflowHealth Health, WorkflowRunInfo? LastFailure, int FailureStreak, IReadOnlyList<WorkflowRunInfo> Runs)
{
    public bool NeedsAttention => Health.NeedsAttention;
    public bool IsStaleFailure => Health.Kind == HealthKind.FailingNow && Health.Stale;

    /// <summary>
    /// Failed again and again without a success in between. This is a run-state signal only; <see cref="WorkflowHealth.Recurring"/> (same error
    /// signature) needs log analysis and is established on demand on the failure-series page, never on the dashboard.
    /// </summary>
    public bool IsRecurring => NeedsAttention && FailureStreak >= 2;

    public bool IsRunning => Health.HasActiveRun;
    public bool IsRecovered => Health.Kind == HealthKind.Recovered;
    public bool IsUnknown => Health.Kind == HealthKind.Unknown;

    /// <summary>Most recent runs on the failing verdict's branch that failed, newest first (cheap timeline for the expanded preview).</summary>
    public IReadOnlyList<WorkflowRunInfo> RecentFailures(int max = 5) =>
        Runs.Where(r => r.State == RunState.Failure && (Health.ScopedToBranch is null || r.Branch == Health.ScopedToBranch)).OrderByDescending(r => r.SortTime).Take(max).ToList();
}

public enum RepoStatus { Failing, Running, Recovered, Healthy, NoRuns }

/// <summary>Analysis of one scanned repository.</summary>
public sealed record RepoTriage(RepositoryInfo Repo, IReadOnlyList<WorkflowTriage> Workflows, int WorkflowCount, double? SuccessRate, DateTimeOffset? LastRunAt, DateTimeOffset LoadedAt)
{
    public int Failing => Workflows.Count(w => w.NeedsAttention);
    public int Recurring => Workflows.Count(w => w.IsRecurring);
    public int Running => Workflows.Count(w => w.IsRunning);
    public int Recovered => Workflows.Count(w => w.IsRecovered);
    public int Unknown => Workflows.Count(w => w.IsUnknown);

    public RepoStatus Status =>
        Failing > 0 ? RepoStatus.Failing
        : Running > 0 ? RepoStatus.Running
        : Recovered > 0 ? RepoStatus.Recovered
        : Workflows.Any(w => w.Health.Kind == HealthKind.Healthy) ? RepoStatus.Healthy
        : RepoStatus.NoRuns;
}

/// <param name="NotChecked">Repositories discovered but not scanned yet (or whose scan failed). Never counted as healthy.</param>
public sealed record PortfolioSummary(int TotalRepositories, int Checked, int NotChecked, int RepositoriesNeedingAttention, int FailingWorkflows, int RecurringWorkflows, int RunningWorkflows, int RecoveredWorkflows, int UnknownWorkflows)
{
    /// <summary>Repositories with nothing proven about them: not scanned, or scanned without a single verdict.</summary>
    public int UnverifiedRepositories => NotChecked + UnverifiedAfterScan;
    public int UnverifiedAfterScan { get; init; }
}

public static class PortfolioTriage
{
    public static RepoTriage Analyze(RepoSnapshot s, DateTimeOffset now, TriageScope? scope = null)
    {
        scope ??= TriageScope.All;
        var runs = s.Runs.AsEnumerable();
        if (!string.IsNullOrEmpty(scope.Event)) runs = runs.Where(r => r.Event == scope.Event);
        if (!string.IsNullOrEmpty(scope.Branch)) runs = runs.Where(r => r.Branch == scope.Branch);
        var scoped = runs.ToList();

        var workflows = WithOrphans(s.Workflows, scoped);
        var byWorkflow = scoped.GroupBy(r => r.WorkflowId).ToDictionary(g => g.Key, g => (IReadOnlyList<WorkflowRunInfo>)g.OrderByDescending(r => r.SortTime).ToList());
        var branch = string.IsNullOrEmpty(scope.Branch) ? s.Repo.DefaultBranch : scope.Branch;

        var list = new List<WorkflowTriage>();
        foreach (var w in workflows)
        {
            byWorkflow.TryGetValue(w.Id, out var wRuns);
            wRuns ??= [];
            if (scope.IsNarrowed && wRuns.Count == 0) continue;
            var health = WorkflowHealthAnalyzer.Assess(wRuns, branch, hasRecurringSeries: null, now);
            list.Add(new WorkflowTriage(s.Repo.Ref, w, health, health.Kind == HealthKind.FailingNow ? health.Verdict : null, FailureStreak(wRuns, health.ScopedToBranch), wRuns));
        }

        var verdicts = Kpis.SuccessRate(scoped);
        return new RepoTriage(s.Repo, list, workflows.Count, verdicts.Rate, scoped.Count == 0 ? null : scoped.Max(r => r.SortTime), s.LoadedAt);
    }

    /// <summary>Consecutive failed verdicts, newest first, on the assessed branch. Cancelled, skipped and active runs neither extend nor break it.</summary>
    public static int FailureStreak(IReadOnlyList<WorkflowRunInfo> runs, string? branch)
    {
        var streak = 0;
        foreach (var r in runs.Where(r => branch is null || r.Branch == branch).OrderByDescending(r => r.SortTime))
        {
            if (!WorkflowHealthAnalyzer.IsVerdict(r.State)) continue;
            if (r.State != RunState.Failure) break;
            streak++;
        }
        return streak;
    }

    /// <summary>Runs whose workflow is missing from the workflow list still count, exactly as in the workspace.</summary>
    private static IReadOnlyList<WorkflowInfo> WithOrphans(IReadOnlyList<WorkflowInfo> wfs, IReadOnlyList<WorkflowRunInfo> runs)
    {
        var known = wfs.Select(w => w.Id).ToHashSet();
        var orphans = runs.Where(r => !known.Contains(r.WorkflowId)).GroupBy(r => r.WorkflowId)
            .Select(g => new WorkflowInfo(g.Key, g.First().Name + " (not in workflow list)", string.IsNullOrEmpty(g.First().WorkflowPath) ? $"unknown/{g.Key}" : g.First().WorkflowPath, true, null));
        return [.. wfs, .. orphans];
    }

    public static bool Matches(WorkflowTriage w, TriageFilter filter) => filter switch
    {
        TriageFilter.NeedsAttention => w.Health.Kind == HealthKind.FailingNow,
        TriageFilter.FailingNow => w.NeedsAttention,
        TriageFilter.Recurring => w.IsRecurring,
        TriageFilter.Recovered => w.IsRecovered,
        TriageFilter.Running => w.IsRunning,
        TriageFilter.Unknown => w.IsUnknown,
        _ => true,
    };

    public static int Count(IEnumerable<RepoTriage> repos, TriageFilter filter) => repos.Sum(r => r.Workflows.Count(w => Matches(w, filter)));

    /// <summary>Rows of the main table: most urgent first (recurring, fresh, running, stale), then newest failure, then names for a stable order.</summary>
    public static IReadOnlyList<WorkflowTriage> Rank(IEnumerable<RepoTriage> repos, TriageFilter filter) =>
        repos.SelectMany(r => r.Workflows).Where(w => Matches(w, filter))
            .OrderBy(w => w.Health.Priority)
            .ThenByDescending(w => w.FailureStreak)
            .ThenByDescending(w => w.LastFailure?.SortTime ?? w.Health.Verdict?.SortTime ?? DateTimeOffset.MinValue)
            .ThenBy(w => w.Repo.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Workflow.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static PortfolioSummary Summarize(IReadOnlyList<RepoTriage> analyzed, int totalRepositories, int notChecked) =>
        new(totalRepositories, analyzed.Count, notChecked,
            analyzed.Count(r => r.Failing > 0),
            analyzed.Sum(r => r.Failing),
            analyzed.Sum(r => r.Recurring),
            analyzed.Sum(r => r.Running),
            analyzed.Sum(r => r.Recovered),
            analyzed.Sum(r => r.Unknown))
        { UnverifiedAfterScan = analyzed.Count(r => r.Status == RepoStatus.NoRuns) };

    public static IReadOnlyList<string> Branches(IEnumerable<RepoSnapshot> snapshots) =>
        snapshots.SelectMany(s => s.Runs).Select(r => r.Branch).Where(b => !string.IsNullOrEmpty(b)).GroupBy(b => b).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).Take(30).ToList();

    public static IReadOnlyList<string> Events(IEnumerable<RepoSnapshot> snapshots) =>
        snapshots.SelectMany(s => s.Runs).Select(r => r.Event).Where(e => !string.IsNullOrEmpty(e)).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList();

    /// <summary>Activity feed: the newest finished or running runs across repositories (one line each, no extra requests).</summary>
    public static IReadOnlyList<(RepoRef Repo, WorkflowRunInfo Run)> RecentActivity(IEnumerable<RepoSnapshot> snapshots, int max = 12) =>
        snapshots.SelectMany(s => s.Runs.Select(r => (s.Repo.Ref, r))).OrderByDescending(x => x.r.SortTime).Take(max).ToList();

    /// <summary>"job › step" of the first failed job and step, from jobs the caller already has. Bounded; never reads a log.</summary>
    public static string? FailureHint(IReadOnlyList<JobInfo> jobs, int maxLength = 120)
    {
        var job = jobs.FirstOrDefault(j => j.State == RunState.Failure);
        if (job is null) return null;
        var step = job.Steps.FirstOrDefault(s => s.State == RunState.Failure);
        var text = step is null ? $"{job.Name} failed" : $"{job.Name} › {step.Name}";
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    /// <summary>First line of a commit message, bounded: the cheap "what changed" summary available without loading jobs.</summary>
    public static string Headline(string? commitMessage, int maxLength = 90)
    {
        var line = (commitMessage ?? "").Split('\n', 2)[0].Trim();
        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }
}
