using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using Microsoft.Extensions.Options;

namespace CIWorkbench.Web;

public enum DataMode { Demo, Live }

public enum DiscoveryState { Idle, Running, Completed, Cancelled, Failed }

/// <summary>
/// Per-circuit state of the standalone host: data mode, progressive repository discovery, selected repository.
/// The GitHub token lives only in server-side options; nothing here is sent to the browser.
/// </summary>
public sealed class AppSession : IDisposable
{
    private readonly IOptions<GitHubOptions> _github;
    private readonly DemoCiProvider _demo;
    private readonly GitHubCiProvider _live;
    private readonly List<RepositoryInfo> _repos = [];
    private readonly List<RepoRef> _recent = [];
    private CancellationTokenSource? _cts;

    public AppSession(IOptions<GitHubOptions> github, DemoCiProvider demo, GitHubCiProvider live, RateLimitTracker tracker, IConfiguration config)
    {
        _github = github; _demo = demo; _live = live; Tracker = tracker;
        var requested = config["CIWORKBENCH_MODE"] ?? config["CIWorkbench:Mode"];
        Mode = string.Equals(requested, "live", StringComparison.OrdinalIgnoreCase) && LiveAvailable ? DataMode.Live : DataMode.Demo;
    }

    public event Action? Changed;
    public RateLimitTracker Tracker { get; }

    public DataMode Mode { get; private set; }
    public bool LiveAvailable => _github.Value.HasToken;
    public ICiProvider Provider => Mode == DataMode.Live ? _live : _demo;

    public IReadOnlyList<RepositoryInfo> Repositories => _repos;
    public IReadOnlyList<RepoRef> Recent => _recent;
    public DiscoveryState Discovery { get; private set; }
    public int PagesLoaded { get; private set; }
    public int? EstimatedPages { get; private set; }
    public string? DiscoveryError { get; private set; }
    public RepoRef? Selected { get; private set; }
    public ConnectionReport? Connection { get; private set; }
    public bool CheckingConnection { get; private set; }
    public RateLimitInfo? RateLimit { get; private set; }

    public void SetMode(DataMode mode)
    {
        if (mode == DataMode.Live && !LiveAvailable) return;
        if (Mode == mode) return;
        CancelDiscovery();
        Mode = mode; _repos.Clear(); Selected = null; Connection = null; RateLimit = null; PagesLoaded = 0; EstimatedPages = null; DiscoveryError = null;
        Discovery = DiscoveryState.Idle;
        Changed?.Invoke();
        _ = StartDiscoveryAsync();
    }

    public void Select(RepoRef? repo)
    {
        if (Selected.Equals(repo)) return;
        Selected = repo;
        if (repo is { } r) { _recent.RemoveAll(x => x.Equals(r)); _recent.Insert(0, r); if (_recent.Count > 6) _recent.RemoveAt(6); }
        Changed?.Invoke();
    }

    public void CancelDiscovery() { _cts?.Cancel(); }

    public async Task StartDiscoveryAsync()
    {
        if (Discovery == DiscoveryState.Running) return;
        _cts?.Dispose(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Discovery = DiscoveryState.Running; DiscoveryError = null;
        if (Discovery == DiscoveryState.Running && _repos.Count > 0) { _repos.Clear(); PagesLoaded = 0; }
        Changed?.Invoke();
        if (Mode == DataMode.Live && !LiveAvailable) { Discovery = DiscoveryState.Failed; DiscoveryError = "No GitHub token configured."; Changed?.Invoke(); return; }
        try
        {
            await foreach (var page in Provider.ListRepositoriesAsync(ct))
            {
                _repos.AddRange(page.Items);
                PagesLoaded = page.PageNumber; EstimatedPages ??= page.EstimatedPages;
                Changed?.Invoke();
            }
            Discovery = DiscoveryState.Completed;
        }
        catch (OperationCanceledException) { Discovery = DiscoveryState.Cancelled; }
        catch (CiProviderException ex) { Discovery = DiscoveryState.Failed; DiscoveryError = ex.Message; }
        Changed?.Invoke();
    }

    public async Task CheckConnectionAsync()
    {
        CheckingConnection = true; Changed?.Invoke();
        try
        {
            Connection = await Provider.CheckConnectionAsync(Selected, CancellationToken.None);
            RateLimit = await Provider.GetRateLimitAsync(CancellationToken.None);
        }
        finally { CheckingConnection = false; Changed?.Invoke(); }
    }

    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
}

/// <summary>Scoped <see cref="ICiProvider"/> that forwards to whichever provider the session currently uses.</summary>
public sealed class SwitchableCiProvider(AppSession session) : ICiProvider
{
    private ICiProvider P => session.Provider;
    public string Name => P.Name;
    public bool IsDemo => P.IsDemo;
    public IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync(CancellationToken ct) => P.ListRepositoriesAsync(ct);
    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) => P.ListWorkflowsAsync(repo, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) => P.ListRecentRunsAsync(repo, max, ct);
    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) => P.ListWorkflowRunsAsync(repo, workflowId, max, ct);
    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) => P.GetRunAsync(repo, runId, ct);
    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => P.ListJobsAsync(repo, runId, ct);
    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) => P.GetJobLogTailAsync(repo, jobId, maxBytes, ct);
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => P.GetFileAsync(repo, path, ct);
    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) => P.GetRateLimitAsync(ct);
    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) => P.CheckConnectionAsync(sampleRepo, ct);
}
