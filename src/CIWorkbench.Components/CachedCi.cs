using CIWorkbench.Core.Models;

namespace CIWorkbench.Components;

/// <summary>
/// Scoped, read-only decorator over the host's <see cref="ICiProvider"/>: a small TTL + LRU cache that also shares in-flight requests.
/// Navigating between the workspace, workflow details and run details therefore re-uses data instead of calling the provider again,
/// and the cache is dropped with the circuit. Nothing here is persisted and no credentials pass through it.
/// </summary>
public sealed class CachedCi(ICiProvider inner, TimeProvider time) : ICiProvider
{
    internal static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(5);     // workflows, files
    public static readonly TimeSpan RunsTtl = TimeSpan.FromSeconds(45);    // run lists / single runs: short, polling invalidates explicitly
    internal static readonly TimeSpan SettledTtl = TimeSpan.FromMinutes(10); // jobs of finished runs, log tails
    internal static readonly TimeSpan LiveTtl = TimeSpan.FromSeconds(15);    // jobs of runs that are still active
    private const int MaxEntries = 400;
    private const int MaxLogs = 24;

    private sealed record Entry(Task<object?> Task, DateTimeOffset Expires, long Seq);

    private readonly Dictionary<string, Entry> _map = [];
    private readonly object _gate = new();
    private long _seq;

    public string Name => inner.Name;
    public bool IsDemo => inner.IsDemo;

    private string Key(string op, params object[] parts) => $"{(inner.IsDemo ? "demo" : "live")}|{op}|{string.Join('|', parts)}";

    private async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct, Func<T, TimeSpan>? ttlFor = null)
    {
        ct.ThrowIfCancellationRequested();
        Task<object?> task;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_map.TryGetValue(key, out var e) && e.Expires > now && !e.Task.IsFaulted && !e.Task.IsCanceled)
            {
                task = e.Task;
                _map[key] = e with { Seq = ++_seq };
            }
            else
            {
                // The shared fetch is deliberately not tied to one caller's token: a caller that navigates away stops waiting, the fetch finishes for the next one.
                task = RunAsync(key, factory, ttlFor);
                _map[key] = new Entry(task, now + ttl, ++_seq);
                Trim();
            }
        }
        try { return (T)(await task.WaitAsync(ct))!; }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            lock (_gate) { if (_map.TryGetValue(key, out var cur) && cur.Task == task) _map.Remove(key); }
            throw;
        }
    }

    private async Task<object?> RunAsync<T>(string key, Func<Task<T>> factory, Func<T, TimeSpan>? ttlFor)
    {
        var value = await factory();
        if (ttlFor is not null)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var e)) _map[key] = e with { Expires = time.GetUtcNow() + ttlFor(value) };
            }
        }
        return value;
    }

    private void Trim()
    {
        if (_map.Count > MaxEntries)
            foreach (var k in _map.OrderBy(x => x.Value.Seq).Take(_map.Count - MaxEntries + 40).Select(x => x.Key).ToList()) _map.Remove(k);
        var logs = _map.Where(x => x.Key.Contains("|log|", StringComparison.Ordinal)).OrderBy(x => x.Value.Seq).ToList();
        if (logs.Count > MaxLogs) foreach (var l in logs.Take(logs.Count - MaxLogs)) _map.Remove(l.Key);
    }

    /// <summary>Drops cached run lists and single runs of one repository (used by refresh); finished-run jobs and logs stay, they cannot change.</summary>
    public void InvalidateRuns(RepoRef repo)
    {
        lock (_gate)
        {
            foreach (var k in _map.Keys.Where(k => k.Contains($"|runs|{repo}", StringComparison.Ordinal) || k.Contains($"|runs-since|{repo}", StringComparison.Ordinal) || k.Contains($"|wfruns|{repo}", StringComparison.Ordinal) || k.Contains($"|run|{repo}", StringComparison.Ordinal)).ToList())
                _map.Remove(k);
        }
    }

    public void Clear() { lock (_gate) _map.Clear(); }
    public int Count { get { lock (_gate) return _map.Count; } }

    // ---- ICiProvider ----

    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => inner.ListRepositoriesAsync(ct);

    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) =>
        GetOrAddAsync(Key("workflows", repo), ListTtl, () => inner.ListWorkflowsAsync(repo, CancellationToken.None), ct);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) =>
        GetOrAddAsync(Key("runs", repo, max), RunsTtl, () => inner.ListRecentRunsAsync(repo, max, CancellationToken.None), ct);

    public Task<RunPeriod> ListRunsSinceAsync(RepoRef repo, DateTimeOffset since, int maxRuns, CancellationToken ct) =>
        GetOrAddAsync(Key("runs-since", repo, since.ToUnixTimeSeconds(), maxRuns), RunsTtl, () => inner.ListRunsSinceAsync(repo, since, maxRuns, CancellationToken.None), ct);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) =>
        GetOrAddAsync(Key("wfruns", repo, workflowId, max), RunsTtl, () => inner.ListWorkflowRunsAsync(repo, workflowId, max, CancellationToken.None), ct);

    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) =>
        GetOrAddAsync(Key("run", repo, runId), RunsTtl, () => inner.GetRunAsync(repo, runId, CancellationToken.None), ct,
            r => r is { State: var s } && s.IsCompleted() ? SettledTtl : RunsTtl);

    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) =>
        GetOrAddAsync(Key("jobs", repo, runId), LiveTtl, () => inner.ListJobsAsync(repo, runId, CancellationToken.None), ct,
            jobs => jobs.Count > 0 && jobs.All(j => j.State.IsCompleted()) ? SettledTtl : LiveTtl);

    public Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct) =>
        GetOrAddAsync(Key("jobs-all", repo, runId), LiveTtl, () => inner.ListJobAttemptsAsync(repo, runId, CancellationToken.None), ct,
            jobs => jobs.Count > 0 && jobs.All(j => j.State.IsCompleted()) ? SettledTtl : LiveTtl);

    public Task<ActionsVariables> GetActionsVariablesAsync(RepoRef repo, CancellationToken ct) =>
        GetOrAddAsync(Key("vars", repo), ListTtl, () => inner.GetActionsVariablesAsync(repo, CancellationToken.None), ct);

    /// <summary>Billing changes slowly and every owner costs a request: reuse it for the same account and month.</summary>
    public Task<BillingUsage> GetBillingUsageAsync(string owner, string? ownerType, int year, int month, CancellationToken ct) =>
        GetOrAddAsync(Key("billing", owner, ownerType ?? "", year, month), SettledTtl, () => inner.GetBillingUsageAsync(owner, ownerType, year, month, CancellationToken.None), ct);

    /// <summary>Forgets cached billing and variables so a manual refresh in Settings reads them again.</summary>
    public void InvalidateRunnerData()
    {
        lock (_gate)
            foreach (var k in _map.Keys.Where(k => k.Contains("|billing|", StringComparison.Ordinal) || k.Contains("|vars|", StringComparison.Ordinal)).ToList()) _map.Remove(k);
    }

    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) =>
        GetOrAddAsync(Key("log", repo, jobId, maxBytes), SettledTtl, () => inner.GetJobLogTailAsync(repo, jobId, maxBytes, CancellationToken.None), ct);

    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) =>
        GetOrAddAsync(Key("file", repo, path), ListTtl, () => inner.GetFileAsync(repo, path, CancellationToken.None), ct);

    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => inner.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => inner.CheckConnectionAsync(sampleRepo, ct);
}

/// <summary>
/// Where the reusable pages live. Hosts with different URL schemes register their own subclass; components never hard-code hrefs.
/// All values are relative (no leading slash) so they resolve against the document base.
/// </summary>
public class CiwRoutes
{
    public virtual string Repository(RepoRef r) => $"repo/{Uri.EscapeDataString(r.Owner)}/{Uri.EscapeDataString(r.Name)}";
    public virtual string Failures(RepoRef r) => $"{Repository(r)}/failures";
    public virtual string Workflow(RepoRef r, long workflowId, string? tab = null) => $"{Repository(r)}/workflows/{workflowId}" + (string.IsNullOrEmpty(tab) ? "" : $"?tab={tab}");
    public virtual string Run(RepoRef r, long runId) => $"{Repository(r)}/runs/{runId}";
    public virtual string Repositories => "repositories";
}
