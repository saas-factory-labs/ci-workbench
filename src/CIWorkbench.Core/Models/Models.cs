namespace CIWorkbench.Core.Models;

public readonly record struct RepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
    public override string ToString() => FullName;
    public static RepoRef Parse(string fullName)
    {
        var i = fullName.IndexOf('/');
        if (i <= 0 || i == fullName.Length - 1) throw new FormatException($"Expected owner/name but got '{fullName}'.");
        return new RepoRef(fullName[..i], fullName[(i + 1)..]);
    }
}

/// <summary>Provider-neutral lifecycle + outcome of a run, job or step.</summary>
public enum RunState { Queued, InProgress, Success, Failure, Cancelled, Skipped, Neutral }

public static class RunStateExtensions
{
    public static bool IsCompleted(this RunState s) => s is not (RunState.Queued or RunState.InProgress);
    public static bool IsActive(this RunState s) => s is RunState.Queued or RunState.InProgress;
}

public sealed record RepositoryInfo(RepoRef Ref, string? Description, string DefaultBranch, bool IsPrivate, string HtmlUrl, DateTimeOffset? PushedAt);

/// <summary>Workflow identity is <see cref="Path"/> (stable); <see cref="Name"/> is display only.</summary>
public sealed record WorkflowInfo(long Id, string Name, string Path, bool IsActive, string? HtmlUrl);

public sealed record WorkflowRunInfo(
    long Id, long WorkflowId, string WorkflowPath, string Name, int RunNumber, int Attempt, RunState State,
    string Event, string Branch, string HeadSha, string? CommitMessage, string Actor,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? UpdatedAt, string HtmlUrl)
{
    public DateTimeOffset SortTime => StartedAt ?? CreatedAt;
    public TimeSpan? Duration => StartedAt is { } s && UpdatedAt is { } u && State.IsCompleted() ? u - s : null;
}

public sealed record StepInfo(int Number, string Name, RunState State);

public sealed record JobInfo(long Id, string Name, RunState State, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<StepInfo> Steps, string HtmlUrl)
{
    public TimeSpan? Duration => StartedAt is { } s && CompletedAt is { } c ? c - s : null;
}

public sealed record RateLimitInfo(int Limit, int Remaining, DateTimeOffset ResetsAt);

public enum CheckStatus { Ok, Failed, Skipped }

public sealed record ConnectionCheck(string Name, CheckStatus Status, string Detail);

public sealed record ConnectionReport(string Account, IReadOnlyList<ConnectionCheck> Checks)
{
    public bool IsHealthy => Checks.All(c => c.Status != CheckStatus.Failed);
}

/// <summary>Thrown with a user-presentable reason when a provider call cannot succeed.</summary>
public sealed class CiProviderException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

