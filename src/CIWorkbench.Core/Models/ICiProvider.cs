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

