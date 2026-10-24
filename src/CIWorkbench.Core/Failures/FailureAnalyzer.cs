using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Failures;

public sealed record FailureAnalysis(IReadOnlyList<FailureSeries> Series, int RunsInspected, int RunsAvailable, bool Truncated)
{
    public IReadOnlyList<FailureSeries> Recurring => Series.Where(s => s.IsRecurring).ToList();
    public IReadOnlyList<FailureSeries> OneOff => Series.Where(s => !s.IsRecurring).ToList();
}

/// <summary>Lazy, bounded analysis: inspects jobs/logs for at most <c>maxRunsToInspect</c> newest failed runs.</summary>
public sealed class FailureAnalyzer(ICiProvider provider)
{
    public const int DefaultMaxRuns = 15;
    public const int LogBytes = 64 * 1024;

    public async Task<FailureAnalysis> AnalyzeAsync(
        RepoRef repo, IReadOnlyList<WorkflowRunInfo> runs, bool windowTruncated, int maxRunsToInspect, CancellationToken ct)
    {
        var failed = runs.Where(r => r.State == RunState.Failure).OrderByDescending(r => r.SortTime).ToList();
        var toInspect = failed.Take(maxRunsToInspect).ToList();
        var observations = new List<FailureObservation>();
        foreach (var run in toInspect)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<JobInfo> jobs;
            try { jobs = await provider.ListJobsAsync(repo, run.Id, ct); }
            catch (CiProviderException) { continue; }
            foreach (var job in jobs.Where(j => j.State == RunState.Failure))
            {
                var step = job.Steps.FirstOrDefault(s => s.State == RunState.Failure);
                string? log = null;
                try { log = await provider.GetJobLogTailAsync(repo, job.Id, LogBytes, ct); }
                catch (CiProviderException) { }
                observations.Add(new FailureObservation(run, job.Name, step?.Name ?? "", SignatureExtractor.Extract(log), job.Id));
            }
        }
        var inspected = toInspect.Select(r => r.Id).ToHashSet();
        var series = FailureSeriesBuilder.Build(repo, observations, runs, inspected, windowTruncated);
        return new FailureAnalysis(series, toInspect.Count, failed.Count, failed.Count > toInspect.Count);
    }
}

