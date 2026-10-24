using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Failures;

public sealed record FailureKey(string Repo, string WorkflowPath, string JobKey, string StepKey, SignatureKind SignatureKind, string? Signature);

/// <summary>One failed job observed in one run.</summary>
public sealed record FailureObservation(WorkflowRunInfo Run, string JobName, string StepName, FailureSignature Signature, long JobId)
{
    public string JobKey => FailureSeriesBuilder.JobKeyOf(JobName);
}

public sealed class FailureSeries
{
    public required FailureKey Key { get; init; }
    public required string Summary { get; init; }
    public string? RawSignature { get; init; }
    public required IReadOnlyList<FailureObservation> Occurrences { get; init; }
    public IReadOnlyList<string> Variants { get; init; } = [];
    public int Count => Occurrences.Count;
    public DateTimeOffset FirstSeen => Occurrences.Min(o => o.Run.SortTime);
    public DateTimeOffset LastSeen => Occurrences.Max(o => o.Run.SortTime);
    public int Streak { get; init; }
    /// <summary>True when the streak reached the edge of the loaded window; render as "≥N".</summary>
    public bool StreakIsLowerBound { get; init; }
    public string JobDisplay => Key.JobKey;
    public string StepDisplay => Key.StepKey;
    public bool CauseUnknown => Key.SignatureKind == SignatureKind.Unknown;
    public bool LogUnavailable => Key.SignatureKind == SignatureKind.LogUnavailable;
    public string Id => $"{Key.WorkflowPath}|{Key.JobKey}|{Key.StepKey}|{Key.Signature}|{Key.SignatureKind}";
}

public static class FailureSeriesBuilder
{
    public const string JobSetupStep = "<job-setup>";

    /// <summary>"build (ubuntu, 20)" -> "build". Matrix values are kept as variants on the series.</summary>
    public static string JobKeyOf(string jobName)
    {
        var i = jobName.IndexOf(" (", StringComparison.Ordinal);
        return (i > 0 && jobName.EndsWith(')') ? jobName[..i] : jobName).Trim();
    }

    public static FailureKey KeyOf(RepoRef repo, FailureObservation o) =>
        new(repo.FullName, o.Run.WorkflowPath, o.JobKey, string.IsNullOrWhiteSpace(o.StepName) ? JobSetupStep : o.StepName,
            o.Signature.Kind, o.Signature.Kind == SignatureKind.Known ? o.Signature.Normalized : null);

    /// <summary>
    /// Groups observations by exact key equality (no fuzzy matching) and computes streaks.
    /// </summary>
    /// <param name="allRuns">The loaded run window (all states) used for streak computation.</param>
    /// <param name="inspectedRunIds">Runs whose jobs were actually inspected; failed runs outside this set stop a streak as a lower bound.</param>
    /// <param name="windowTruncated">True when older runs exist beyond <paramref name="allRuns"/>.</param>
    public static IReadOnlyList<FailureSeries> Build(
        RepoRef repo,
        IReadOnlyList<FailureObservation> observations,
        IReadOnlyList<WorkflowRunInfo> allRuns,
        IReadOnlySet<long> inspectedRunIds,
        bool windowTruncated)
    {
        var result = new List<FailureSeries>();
        foreach (var group in observations.GroupBy(o => KeyOf(repo, o)))
        {
            var key = group.Key;
            var occ = group.OrderByDescending(o => o.Run.SortTime).ToList();
            var (streak, lower) = ComputeStreak(key, occ, allRuns, inspectedRunIds, windowTruncated);
            var rawSample = occ.Select(o => o.Signature.Raw).FirstOrDefault(r => r is not null);
            var variants = occ.Select(o => o.JobName).Where(n => n != key.JobKey).Distinct().OrderBy(n => n).ToList();
            result.Add(new FailureSeries
            {
                Key = key,
                Occurrences = occ,
                Variants = variants,
                RawSignature = rawSample,
                Summary = Summarize(key, rawSample),
                Streak = streak,
                StreakIsLowerBound = lower,
            });
        }
        return result.OrderByDescending(s => s.LastSeen).ThenByDescending(s => s.Count).ToList();
    }

    private static string Summarize(FailureKey key, string? raw) => key.SignatureKind switch
    {
        SignatureKind.Known => $"{key.JobKey} › {key.StepKey}: {Truncate(raw ?? key.Signature ?? "", 120)}",
        SignatureKind.Unknown => $"{key.JobKey} › {key.StepKey}: same step failing, cause unknown",
        _ => $"{key.JobKey} › {key.StepKey}: failing (log unavailable)",
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Walks the series workflow's runs newest to oldest. Cancelled/skipped/active runs are ignored;
    /// a success or a different outcome ends the streak; an uninspected failed run or the window edge makes it a lower bound.
    /// </summary>
    internal static (int Streak, bool LowerBound) ComputeStreak(
        FailureKey key, IReadOnlyList<FailureObservation> occurrences, IReadOnlyList<WorkflowRunInfo> allRuns,
        IReadOnlySet<long> inspected, bool windowTruncated)
    {
        var hitRuns = occurrences.Select(o => o.Run.Id).ToHashSet();
        var ordered = allRuns.Where(r => r.WorkflowPath == key.WorkflowPath).OrderByDescending(r => r.SortTime).ToList();
        var streak = 0;
        foreach (var run in ordered)
        {
            if (!run.State.IsCompleted() || run.State is RunState.Cancelled or RunState.Skipped or RunState.Neutral) continue;
            if (hitRuns.Contains(run.Id)) { streak++; continue; }
            if (run.State == RunState.Failure && !inspected.Contains(run.Id)) return (streak, true);
            return (streak, false);
        }
        return (streak, windowTruncated);
    }
}

