namespace CIWorkbench.Core.Models;

/// <summary>
/// Read-only seam over a CI system. Implemented by the demo provider and the GitHub provider.
/// Instances handed to UI components are expected to be already authorized for the current viewer.
/// </summary>
public interface ICiProvider
{
    string Name { get; }
    bool IsDemo { get; }

    /// <summary>Streams repository pages so the UI can render progressively. Honors cancellation.</summary>
    IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct);
    /// <summary>Bounded repo-wide recent runs (one paged request series, not one per workflow).</summary>
    Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct);
    Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct);
    /// <summary>One run by id (deep links), or null when it does not exist or is not visible.</summary>
    Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct);
    Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct);
    /// <summary>
    /// Jobs of every attempt of a run (rerun attempts were billed too; <see cref="ListJobsAsync"/> only returns the latest). Deduplicate by <see cref="JobInfo.Id"/>.
    /// Providers that cannot tell fall back to the latest attempt.
    /// </summary>
    Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct) => ListJobsAsync(repo, runId, ct);
    /// <summary>
    /// Runs created since <paramref name="since"/> (up to <paramref name="maxRuns"/>), paged. The result says whether it is the complete set
    /// (GitHub caps filtered run listings at 1,000 results). Providers that cannot filter by date fall back to their recent-runs window.
    /// </summary>
    async Task<RunPeriod> ListRunsSinceAsync(RepoRef repo, DateTimeOffset since, int maxRuns, CancellationToken ct)
    {
        var runs = await ListRecentRunsAsync(repo, maxRuns, ct);
        return new RunPeriod(runs.Where(r => r.CreatedAt >= since).ToList(), null, runs.Count < maxRuns || runs.Any(r => r.CreatedAt < since));
    }
    /// <summary>Actions variables visible for a repository (repository plus shared organisation variables). Optional permission: a refusal is a result, not an exception.</summary>
    Task<ActionsVariables> GetActionsVariablesAsync(RepoRef repo, CancellationToken ct) => Task.FromResult(ActionsVariables.Unsupported);
    /// <summary>Official GitHub billing usage of one account for one calendar month. Optional permission: a refusal is a result, not an exception.</summary>
    Task<BillingUsage> GetBillingUsageAsync(string owner, string? ownerType, int year, int month, CancellationToken ct) => Task.FromResult(BillingUsage.Unsupported);
    /// <summary>Last <paramref name="maxBytes"/> of a job log, or null when unavailable.</summary>
    Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct);
    /// <summary>Raw file content at the default branch, or null if the file does not exist.</summary>
    Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct);
    Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct);
    /// <summary>Verifies each capability with the real API operation, one check per capability.</summary>
    Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct);
}

/// <summary>One page of repositories. <see cref="EstimatedPages"/> is null when unknown (never fake a total).</summary>
public sealed record RepositoryPage(IReadOnlyList<RepositoryInfo> Items, int PageNumber, int? EstimatedPages, bool IsLast);

