using CIWorkbench.Components;
using CIWorkbench.Core.Models;
using CIWorkbench.Core.Overview;

namespace CIWorkbench.Web;

/// <summary>Where a repository stands in the dashboard's scan. A repository without an entry has not been checked and is never treated as healthy.</summary>
public enum ScanState { NotChecked, Loading, Loaded, Failed }

/// <param name="Snapshot">Last loaded data. Kept while a refresh is running so the page never flashes back to empty.</param>
public sealed record RepoScan(RepositoryInfo Repo, ScanState State, RepoSnapshot? Snapshot, string? Error);

/// <summary>
/// Progressive, bounded loader behind the global Overview. Per repository it makes the same two calls the workspace does
/// (workflows, then one window of recent runs) through the shared <see cref="CachedCi"/>, so opening a repository afterwards reuses them.
/// It never loads jobs or logs by itself; the failing step of a row is fetched only when that row is expanded.
/// At most <see cref="Concurrency"/> repositories load at once and only the first <see cref="BatchSize"/> (then more on request) are scanned,
/// so 100+ repositories cost a handful of requests instead of hundreds. The scan is per circuit and is dropped when the data source changes.
/// </summary>
public sealed class OverviewData(AppSession session, CachedCi ci, TimeProvider time) : IDisposable
{
    public const int BatchSize = 12;
    public const int Concurrency = 3;
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshThrottle = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<RepoRef, RepoScan> _scans = [];
    private readonly HashSet<RepoRef> _queued = [];            // loaded repositories waiting for a refresh
    private readonly Dictionary<long, string> _hints = [];     // run id -> failing "job › step" ("" = none found)
    private readonly HashSet<long> _hintsLoading = [];
    private CancellationTokenSource _cts = new();
    private int _generation, _running, _source, _target = BatchSize;
    private bool _attached;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public event Action? Changed;
    /// <summary>Increases with every visible change; pages use it to memoise derived view models.</summary>
    public int Version { get; private set; }

    /// <summary>Same window the repository page requests, so both share cache entries.</summary>
    public int RunWindow => session.Mode == DataMode.Demo ? 250 : 100;

    /// <summary>Repositories in scope (organisation filter), newest push first.</summary>
    public IReadOnlyList<RepositoryInfo> ScopedRepositories() =>
        session.Repositories.Where(r => session.OrgFilter is null || r.Ref.Owner.Equals(session.OrgFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue).ThenBy(r => r.Ref.FullName, StringComparer.OrdinalIgnoreCase).ToList();

    public RepoScan? ScanOf(RepoRef repo) { lock (_gate) return _scans.GetValueOrDefault(repo); }

    /// <summary>Everything that has data, in scope.</summary>
    public IReadOnlyList<RepoSnapshot> Snapshots()
    {
        var scope = ScopedRepositories().Select(r => r.Ref).ToHashSet();
        lock (_gate) return _scans.Values.Where(s => s.Snapshot is not null && scope.Contains(s.Repo.Ref)).Select(s => s.Snapshot!).ToList();
    }

    public int FailedCount { get { lock (_gate) return _scans.Values.Count(s => s.State == ScanState.Failed && s.Snapshot is null); } }

    /// <summary>A scan or refresh is in flight or waiting for a free slot.</summary>
    public bool Busy { get { lock (_gate) return _running > 0 || _queued.Count > 0 || PendingCount() > 0; } }

    /// <summary>Repositories in scope beyond the part of the list that is scanned so far.</summary>
    public int Remaining => Math.Max(0, ScopedRepositories().Count - _target);
    public int ScanTarget => _target;
    public bool HasToken => session.LiveAvailable;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>The Overview page is on screen: follow session changes and keep the scan going.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _source = session.SourceVersion;
        session.Changed += OnSession;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var s in _scans.Values.Where(s => s.Snapshot is not null && now - s.Snapshot.LoadedAt > Stale)) _queued.Add(s.Repo.Ref);
        }
        Pump();
    }

    public void Detach() { _attached = false; session.Changed -= OnSession; }

    private void OnSession()
    {
        if (!_attached) return;
        if (_source != session.SourceVersion) Reset();
        Pump();
        Raise();
    }

    /// <summary>The data source changed: nothing loaded so far describes the new one.</summary>
    private void Reset()
    {
        _source = session.SourceVersion;
        lock (_gate)
        {
            _generation++; _cts.Cancel(); _cts = new CancellationTokenSource();
            _scans.Clear(); _queued.Clear(); _hints.Clear(); _hintsLoading.Clear(); _running = 0; _target = BatchSize;
        }
    }

    public void Dispose() { Detach(); lock (_gate) { _generation++; _cts.Cancel(); } }

    // ---------------------------------------------------------------- scanning

    private IEnumerable<RepositoryInfo> Target() => ScopedRepositories().Take(_target);

    private int PendingCount()
    {
        if (!_attached) return 0;
        return Target().Count(r => !_scans.TryGetValue(r.Ref, out var s) || (s.State == ScanState.NotChecked) || _queued.Contains(r.Ref) && s.State != ScanState.Loading);
    }

    /// <summary>Starts loads until <see cref="Concurrency"/> are running. Called on attach, on every session change and after each load finishes.</summary>
    private void Pump()
    {
        List<RepositoryInfo> start = [];
        int gen; CancellationToken ct;
        lock (_gate)
        {
            if (!_attached) return;
            gen = _generation; ct = _cts.Token;
            var slots = Concurrency - _running;
            foreach (var repo in Target())
            {
                if (slots <= 0) break;
                var has = _scans.TryGetValue(repo.Ref, out var s);
                var needs = !has || s!.State == ScanState.NotChecked || (_queued.Contains(repo.Ref) && s!.State != ScanState.Loading);
                if (!needs) continue;
                _scans[repo.Ref] = new RepoScan(repo, ScanState.Loading, s?.Snapshot, null);
                _queued.Remove(repo.Ref);
                _running++; slots--; start.Add(repo);
            }
        }
        foreach (var repo in start) _ = ScanAsync(repo, gen, ct);
    }

    private async Task ScanAsync(RepositoryInfo repo, int gen, CancellationToken ct)
    {
        RepoScan result;
        try
        {
            var wfTask = ci.ListWorkflowsAsync(repo.Ref, ct);
            var runsTask = ci.ListRecentRunsAsync(repo.Ref, RunWindow, ct);
            var wfs = await wfTask; var runs = await runsTask;
            result = new RepoScan(repo, ScanState.Loaded, new RepoSnapshot(repo, wfs, runs, time.GetUtcNow()), null);
        }
        catch (OperationCanceledException) { lock (_gate) { if (gen == _generation) _running--; } return; }
        catch (CiProviderException ex)
        {
            RepoSnapshot? old; lock (_gate) old = _scans.GetValueOrDefault(repo.Ref)?.Snapshot;
            result = new RepoScan(repo, ScanState.Failed, old, ex.Message);
        }
        catch (Exception)
        {
            // A fire-and-forget scan must never leave its slot taken: report a generic failure (no exception text, it may carry URLs).
            RepoSnapshot? old; lock (_gate) old = _scans.GetValueOrDefault(repo.Ref)?.Snapshot;
            result = new RepoScan(repo, ScanState.Failed, old, "Could not load this repository.");
        }
        lock (_gate)
        {
            if (gen != _generation) return;                  // the source changed while this was loading: drop it silently
            _scans[repo.Ref] = result; _running--;
        }
        Pump();
        Raise();
    }

    /// <summary>Reloads what was scanned (runs only; workflow lists stay cached for minutes). Throttled like the workspace's refresh.</summary>
    public void Refresh()
    {
        var now = time.GetUtcNow();
        if (now - _lastRefresh < RefreshThrottle) return;
        _lastRefresh = now;
        lock (_gate)
        {
            foreach (var s in _scans.Values.Where(s => s.State != ScanState.Loading).ToList())
            {
                ci.InvalidateRuns(s.Repo.Ref);
                if (s.State == ScanState.Failed && s.Snapshot is null) _scans.Remove(s.Repo.Ref); else _queued.Add(s.Repo.Ref);
            }
        }
        Pump();
        Raise();
    }

    /// <summary>Scans the next batch of repositories.</summary>
    public void LoadMore()
    {
        _target += BatchSize;
        Pump();
        Raise();
    }

    // ---------------------------------------------------------------- failing step of one row (on demand)

    public string? HintFor(WorkflowRunInfo run) { lock (_gate) return _hints.TryGetValue(run.Id, out var h) ? h : null; }

    /// <summary>One <c>ListJobs</c> call for one failed run; no logs. Safe to call repeatedly.</summary>
    public async Task LoadHintAsync(RepoRef repo, WorkflowRunInfo run)
    {
        int gen;
        lock (_gate) { if (_hints.ContainsKey(run.Id) || !_hintsLoading.Add(run.Id)) return; gen = _generation; }
        string hint;
        try { hint = PortfolioTriage.FailureHint(await ci.ListJobsAsync(repo, run.Id, CancellationToken.None)) ?? ""; }
        catch (CiProviderException) { hint = ""; }
        lock (_gate) { _hintsLoading.Remove(run.Id); if (gen == _generation) _hints[run.Id] = hint; else return; }
        Raise();
    }

    private void Raise() { Version++; Changed?.Invoke(); }
}
