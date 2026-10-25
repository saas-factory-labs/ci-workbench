using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CIWorkbench.Components;
using CIWorkbench.Core.Models;
using CIWorkbench.Core.Overview;
using CIWorkbench.Core.Runners;
using CIWorkbench.GitHub;
using Microsoft.Extensions.Options;

namespace CIWorkbench.Web;

/// <summary>
/// Process-wide memory of finished jobs. A completed run's jobs never change, so they are fetched once and reused across circuits and page loads.
/// Entries are partitioned by an access context (data source, token fingerprint, allowlist), so one user's data is never served for another
/// token or for the other data source. Bounded; the oldest entries are dropped first. Nothing is persisted.
/// </summary>
public sealed class RunnerJobStore
{
    private const int MaxEntries = 50_000;
    private readonly ConcurrentDictionary<string, IReadOnlyList<JobSample>> _map = new();
    private readonly ConcurrentQueue<string> _order = new();

    public int Count => _map.Count;
    public bool TryGet(string key, out IReadOnlyList<JobSample> jobs) => _map.TryGetValue(key, out jobs!);

    public void Set(string key, IReadOnlyList<JobSample> jobs)
    {
        if (_map.TryAdd(key, jobs)) _order.Enqueue(key);
        while (_map.Count > MaxEntries && _order.TryDequeue(out var oldest)) _map.TryRemove(oldest, out _);
    }

    /// <summary>Partition key. The token itself is only hashed, in memory, and never leaves the process.</summary>
    public static string ContextOf(DataMode mode, GitHubOptions github)
    {
        if (mode == DataMode.Demo) return "demo";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(github.ApiBaseUrl + "\n" + github.Token)))[..12];
        return $"live|{fingerprint}|{string.Join(',', github.AllowedRepositories.Order(StringComparer.OrdinalIgnoreCase))}";
    }
}

/// <summary>Plans of the synthetic demo accounts. These are demo assumptions, shown as such; live mode never gets a default plan.</summary>
public static class DemoRunnerPlans
{
    public static Dictionary<string, ProviderPlanOptions> Create() => new(StringComparer.OrdinalIgnoreCase)
    {
        // example-org states its plan; example-labs deliberately has none ("Allowance unknown").
        [$"{RunnerCatalog.GitHub}.example-org"] = new() { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 2000, AllowanceUnit = UsageUnit.Minutes, Confirmed = true, Note = "Demo plan: 2,000 included minutes." },
        [RunnerCatalog.Tenki] = new() { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 10, AllowanceUnit = UsageUnit.UsdCredits, RatePerUnit = 0.002, Note = "Demo assumption: legacy $10 monthly credit at $0.002 per core-minute." },
        [RunnerCatalog.Avrea] = new() { Allowance = AllowanceKind.MonthlyIncluded, AllowanceAmount = 3000, AllowanceUnit = UsageUnit.NormalizedMinutes, Confirmed = true },
        [RunnerCatalog.Celesto] = new() { Allowance = AllowanceKind.Prepaid, AllowanceUnit = UsageUnit.Minutes, Note = "Demo assumption: prepaid credit; balance not entered." },
    };
}

public static class RunnerInsightsServiceCollectionExtensions
{
    /// <summary>Registers Runner Insights (a Community feature: no licence check). Configuration comes from the <c>RunnerInsights</c> section.</summary>
    public static IServiceCollection AddRunnerInsights(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RunnerInsightsOptions>(configuration.GetSection(RunnerInsightsOptions.Section));
        services.AddSingleton<RunnerJobStore>();
        services.AddSingleton<RunnerPlanStore>();
        services.AddScoped<RunnerInsightsData>();
        return services;
    }
}

public enum RunnerScanState { Idle, WaitingForOverview, Measuring, Done }

/// <param name="IsHistorical">The workflow no longer exists in the current workflow list; only its past jobs are known.</param>
/// <param name="DefinitionUnreadable">The workflow file could not be read. That says nothing about its runners being missing or invalid.</param>
public sealed record WorkflowRunnerRow(WorkflowInfo Workflow, IReadOnlyList<ConfiguredRunner> Configured, bool DefinitionUnreadable, bool IsHistorical, IReadOnlyList<WorkflowUsage> Actual);

public sealed record RepoRunnerView(
    RepoRef Repo, bool IsPrivate, RunnerUsageReport Usage, IReadOnlyList<WorkflowRunnerRow> Workflows, ActionsVariables Variables,
    int FilesRead, int FilesTotal, UsageCoverage Coverage, DateTimeOffset UpdatedAt);

/// <summary>One editable allowance in Settings: which provider (and, for GitHub, which account) it belongs to and where its value comes from.</summary>
public sealed record PlanRow(string Key, RunnerProviderDef Provider, string? Pool, ProviderPlanOptions? Plan, PlanSource Source, IReadOnlyList<PlanSuggestion> Suggestions)
{
    public string Title => Pool is null ? Provider.Name : $"{Provider.Name} · {Pool}";
}

/// <summary>
/// Runner usage for the Overview, Settings and repository pages. It lists each repository's runs of the billing period (paged, cached), then
/// fetches one job request per finished run it has not seen, newest first, under a per-pass budget that also stops at a rate-limit reserve.
/// Collection is resumable: finished jobs are remembered process-wide, a refresh only fetches what is new, and whatever is still missing is
/// reported as Partial with the number of runs left. Mode switches and credential changes drop everything except the context-partitioned stores.
/// </summary>
public sealed class RunnerInsightsData(
    AppSession session, OverviewData overview, CachedCi ci, RunnerJobStore store, RunnerPlanStore plans,
    IOptions<RunnerInsightsOptions> configured, IOptions<GitHubOptions> github, TimeProvider time) : IDisposable
{
    private sealed class Budget(int n)
    {
        private int _n = n;
        public bool TryTake() => Interlocked.Decrement(ref _n) >= 0;
        public void Exhaust() => Volatile.Write(ref _n, -1);
    }

    private sealed record Engine(RunnerInsightsOptions Options, RunnerCatalog Catalog, RunnerUsageEstimator Estimator, RunnerUsageAggregator Aggregator);
    private sealed record Listed(RunPeriod Period, DateTimeOffset At, DateTimeOffset Since);

    private readonly object _gate = new();
    private readonly Dictionary<DataMode, Engine> _engines = [];
    private readonly Dictionary<string, BillingUsage> _billing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RepoRef, Listed> _listed = [];
    private readonly HashSet<RepoRef> _failed = [];
    private readonly Dictionary<RepoRef, (RepoRunnerView View, DateTimeOffset At)> _repoViews = [];
    private CancellationTokenSource _cts = new();
    private int _generation, _source;
    private bool _attached, _measuring, _planHooked;
    private DateTimeOffset? _lastPass;
    private int _lastScope = -1, _lastOverviewVersion = -1;

    public event Action? Changed;
    public int Version { get; private set; }
    public RunnerScanState State { get; private set; }
    public RunnerUsageReport Report { get; private set; } = RunnerUsageReport.Empty(DateTimeOffset.MinValue);
    public string? Note { get; private set; }
    public int PendingRuns { get; private set; }

    /// <summary>The effective options for the current data source: server configuration over saved allowances (demo: the demo plans).</summary>
    public RunnerInsightsOptions Options => EngineOf().Options;
    public RunnerCatalog Catalog => EngineOf().Catalog;
    public bool IsMeasuring => State == RunnerScanState.Measuring;
    public bool HasData => Report.Providers.Count > 0 || Report.BillingByOwner.Count > 0;
    /// <summary>Settings may edit allowances only for a source that has an identity to keep them under.</summary>
    public bool CanEditPlans => session.Mode == DataMode.Demo || session.LiveAvailable;

    private string Context => RunnerJobStore.ContextOf(session.Mode, github.Value);
    private static string KeyOf(string ctx, RepoRef repo, WorkflowRunInfo run) => $"{ctx}|{repo}|{run.Id}|{run.Attempt}";

    private Engine EngineOf()
    {
        lock (_gate)
        {
            if (_engines.TryGetValue(session.Mode, out var e)) return e;
            var demo = session.Mode == DataMode.Demo;
            if (demo) plans.SeedDemo("demo", DemoRunnerPlans.Create());
            var server = demo ? new RunnerInsightsOptions() : configured.Value;
            var merged = new Dictionary<string, ProviderPlanOptions>(plans.Get(Context), StringComparer.OrdinalIgnoreCase);
            if (!demo) foreach (var (k, v) in server.Providers) merged[k] = v;                      // server configuration always wins
            var o = new RunnerInsightsOptions
            {
                MaxRunsPerRepository = server.MaxRunsPerRepository, MaxJobRequestsPerRefresh = server.MaxJobRequestsPerRefresh, MaxJobRequestsPerCollect = server.MaxJobRequestsPerCollect,
                RateLimitReserve = server.RateLimitReserve, Concurrency = server.Concurrency, RefreshMinutes = server.RefreshMinutes, BillingEnabled = server.BillingEnabled,
                DataDirectory = server.DataDirectory, Providers = merged, CustomRules = server.CustomRules,
            };
            var catalog = new RunnerCatalog(o.CustomRules);
            return _engines[session.Mode] = new Engine(o, catalog, new RunnerUsageEstimator(catalog, o), new RunnerUsageAggregator(catalog, o));
        }
    }

    // ---------------------------------------------------------------- allowances (Settings)

    public PlanSource SourceOf(string planKey) => !CanEditPlans ? PlanSource.None
        : session.Mode == DataMode.Live && configured.Value.Providers.ContainsKey(planKey) ? PlanSource.Server
        : plans.Has(Context, planKey) ? PlanSource.Saved : PlanSource.None;

    /// <summary>One row per provider; GitHub gets one per account because included minutes belong to the account, not to a repository.</summary>
    public IReadOnlyList<PlanRow> PlanRows()
    {
        var e = EngineOf(); var rows = new List<PlanRow>();
        var owners = session.Repositories.Select(r => r.Ref.Owner).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        foreach (var def in e.Catalog.Providers.Where(d => d.Id is not RunnerCatalog.Custom and not RunnerCatalog.GitHubLarger and not RunnerCatalog.SelfHosted))
        {
            if (def.Id == RunnerCatalog.GitHub)
            {
                foreach (var owner in owners)
                {
                    var key = $"{def.Id}.{owner}";
                    rows.Add(new PlanRow(key, def, owner, e.Options.PlanOf(def.Id, owner), SourceOf(key), PlanSuggestions.For(def.Id)));
                }
                if (owners.Count == 0) rows.Add(new PlanRow(def.Id, def, null, e.Options.PlanOf(def.Id), SourceOf(def.Id), PlanSuggestions.For(def.Id)));
            }
            else rows.Add(new PlanRow(def.Id, def, null, e.Options.PlanOf(def.Id), SourceOf(def.Id), PlanSuggestions.For(def.Id)));
        }
        return rows;
    }

    /// <summary>Validates and saves one allowance on the server. Returns the problems to show; empty when saved.</summary>
    public IReadOnlyList<string> SavePlan(string planKey, ProviderPlanOptions plan)
    {
        if (!CanEditPlans) return ["Allowances can be saved once a data source with an identity is active (connect GitHub first)."];
        if (SourceOf(planKey) == PlanSource.Server) return ["This allowance is set by server configuration and cannot be changed here."];
        var provider = Catalog.Get(planKey.Split('.')[0]);
        var errors = PlanValidator.Validate(provider, plan, time.GetUtcNow());
        if (errors.Count > 0) return errors;
        plans.Save(Context, planKey, plan);
        OnPlansChanged();
        return [];
    }

    public bool ResetPlan(string planKey)
    {
        if (SourceOf(planKey) != PlanSource.Saved) return false;
        var removed = plans.Reset(Context, planKey);
        if (removed) OnPlansChanged();
        return removed;
    }

    private void OnPlansChanged()
    {
        lock (_gate) _engines.Remove(session.Mode);          // rebuilt with the new allowances; collected jobs are untouched
        Recompute(); Raise();
    }

    // ---------------------------------------------------------------- lifecycle

    public void Attach()
    {
        if (_attached) return;
        _attached = true; _source = session.SourceVersion;
        session.Changed += OnSession; overview.Changed += OnOverview;
        if (!_planHooked) { plans.Changed += OnStoreChanged; _planHooked = true; }
        OnOverview();
    }

    public void Detach() { _attached = false; session.Changed -= OnSession; overview.Changed -= OnOverview; }

    public void Dispose() { Detach(); if (_planHooked) plans.Changed -= OnStoreChanged; lock (_gate) { _generation++; _cts.Cancel(); } }

    private void OnStoreChanged() { /* another circuit saved: rebuild lazily on the next recompute */ lock (_gate) _engines.Remove(session.Mode); }

    private void OnSession()
    {
        if (!_attached) return;
        if (_source != session.SourceVersion) Reset();
        else if (overview.Version != _lastOverviewVersion) OnOverview();
    }

    /// <summary>The data source changed: nothing measured so far describes the new one.</summary>
    private void Reset()
    {
        _source = session.SourceVersion;
        lock (_gate)
        {
            _generation++; _cts.Cancel(); _cts = new CancellationTokenSource();
            _billing.Clear(); _failed.Clear(); _repoViews.Clear(); _listed.Clear(); _engines.Clear(); _measuring = false;
            _lastPass = null; _lastScope = -1; _lastOverviewVersion = -1; PendingRuns = 0; Note = null;
            State = RunnerScanState.Idle; Report = RunnerUsageReport.Empty(time.GetUtcNow());
        }
        Raise();
    }

    private void OnOverview()
    {
        if (!_attached) return;
        if (_source != session.SourceVersion) Reset();         // whichever of the two session subscribers runs first, nothing stale survives a mode switch
        _lastOverviewVersion = overview.Version;
        Recompute();
        Evaluate();
        Raise();
    }

    private void Evaluate()
    {
        if (_measuring) return;
        if (overview.Busy) { State = RunnerScanState.WaitingForOverview; return; }
        var scope = overview.Snapshots().Count;
        if (scope == 0) { State = RunnerScanState.Idle; return; }
        var now = time.GetUtcNow();
        if (_lastPass is null || now - _lastPass > TimeSpan.FromMinutes(Options.RefreshMinutes) || scope != _lastScope) StartPass(Options.MaxJobRequestsPerRefresh, relist: _lastPass is not null);
        else State = RunnerScanState.Done;
    }

    /// <summary>Re-lists the period's runs and re-reads billing (Settings / Overview "Refresh"), then continues collecting what is still missing.</summary>
    public void Refresh()
    {
        if (!_attached || _measuring) return;
        ci.InvalidateRunnerData();
        lock (_gate) { _billing.Clear(); _repoViews.Clear(); }
        overview.Refresh();
        StartPass(Options.MaxJobRequestsPerRefresh, relist: true);
    }

    /// <summary>Explicit "Collect remaining": a larger budget for the runs still missing. Safe to repeat; it resumes where the last pass stopped.</summary>
    public void CollectRemaining()
    {
        if (!_attached || _measuring || overview.Busy) return;
        StartPass(Options.MaxJobRequestsPerCollect, relist: false);
    }

    // ---------------------------------------------------------------- collecting

    private void StartPass(int jobBudget, bool relist)
    {
        int gen; CancellationToken ct;
        lock (_gate)
        {
            if (_measuring) return;
            _measuring = true; gen = _generation; ct = _cts.Token;
            _lastPass = time.GetUtcNow(); _lastScope = overview.Snapshots().Count; Note = null;
            State = RunnerScanState.Measuring;
        }
        Raise();
        _ = PassAsync(gen, ct, jobBudget, relist);
    }

    /// <summary>True while the GitHub rate limit still has room above the configured reserve (always true for demo data).</summary>
    private bool RateOk(RunnerInsightsOptions o, out DateTimeOffset? resumesAt)
    {
        resumesAt = null;
        if (session.Mode == DataMode.Demo) return true;
        if (session.Tracker.Last is { } l && l.Remaining <= o.RateLimitReserve && l.ResetsAt > time.GetUtcNow()) { resumesAt = l.ResetsAt; return false; }
        return true;
    }

    private async Task PassAsync(int gen, CancellationToken ct, int jobBudget, bool relist)
    {
        string? note = null;
        try
        {
            var e = EngineOf(); var now = time.GetUtcNow(); var ctx = Context;
            var budget = new Budget(Math.Max(1, jobBudget));
            var billingTask = LoadBillingAsync(e.Options, now, gen, ct);

            await ListPeriodsAsync(e, now, relist, ct);
            if (gen == _generation) { Recompute(); Raise(); }

            var (_, pending, _, _) = Collect(overview.Snapshots(), e, now, ctx);
            using var gate = new SemaphoreSlim(Math.Clamp(e.Options.Concurrency, 1, 8));
            var work = pending.Select(async p =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    if (!RateOk(e.Options, out var resumes)) { note = $"Paused to keep a GitHub rate-limit reserve; resumes after {resumes:HH:mm} UTC. Use Collect remaining then."; return; }
                    if (!budget.TryTake()) return;
                    await FetchRunAsync(p.Repo, p.Run, ctx, ct);
                    if (gen == _generation) { Recompute(); Raise(); }       // progressive: the numbers grow while the pass runs
                }
                catch (CiProviderException ex)
                {
                    lock (_gate) _failed.Add(p.Repo.Ref);
                    if (ex.StatusCode is 429 || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) { note = "Stopped early: GitHub's rate limit was reached."; budget.Exhaust(); }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(work);
            await billingTask;
            if (gen != _generation) return;
            Note = note;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (gen == _generation) Note = "Runner usage could not be measured completely."; }
        finally
        {
            lock (_gate) { if (gen == _generation) { _measuring = false; State = RunnerScanState.Done; } }
        }
        if (gen == _generation) { Recompute(); Raise(); }
    }

    /// <summary>Lists the billing period's runs of every repository in scope (one request per 100 runs; cached and ETag-revalidated).</summary>
    private async Task ListPeriodsAsync(Engine e, DateTimeOffset now, bool relist, CancellationToken ct)
    {
        var since = WindowStart(e.Options, now);
        var stale = overview.Snapshots().Select(s => s.Repo.Ref).Where(r =>
        {
            lock (_gate) return relist || !_listed.TryGetValue(r, out var l) || l.Since != since || now - l.At > TimeSpan.FromMinutes(e.Options.RefreshMinutes);
        }).ToList();
        using var gate = new SemaphoreSlim(Math.Clamp(e.Options.Concurrency, 1, 8));
        await Task.WhenAll(stale.Select(async repo =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!RateOk(e.Options, out _)) return;
                var period = await ci.ListRunsSinceAsync(repo, since, Math.Max(100, e.Options.MaxRunsPerRepository), ct);
                lock (_gate) { _listed[repo] = new Listed(period, now, since); _failed.Remove(repo); }
            }
            catch (CiProviderException) { lock (_gate) _failed.Add(repo); }
            finally { gate.Release(); }
        }));
    }

    private async Task FetchRunAsync(RepositoryInfo repo, WorkflowRunInfo run, string ctx, CancellationToken ct)
    {
        var jobs = await ci.ListJobAttemptsAsync(repo.Ref, run.Id, ct);      // filter=all: earlier attempts of a rerun were billed too
        store.Set(KeyOf(ctx, repo.Ref, run), jobs.Select(j => ToSample(repo, run, j)).ToList());
    }

    internal static JobSample ToSample(RepositoryInfo repo, WorkflowRunInfo run, JobInfo j) =>
        new(repo.Ref, repo.IsPrivate, run.Id, run.WorkflowId, run.WorkflowPath, run.Name, j.Id, j.Name, j.RunAttempt, j.Labels ?? [], j.RunnerName, j.RunnerGroup, j.State, j.CreatedAt, j.StartedAt, j.CompletedAt, j.HtmlUrl);

    private async Task LoadBillingAsync(RunnerInsightsOptions o, DateTimeOffset now, int gen, CancellationToken ct)
    {
        if (!o.BillingEnabled) return;
        var owners = overview.ScopedRepositories().GroupBy(r => r.Ref.Owner, StringComparer.OrdinalIgnoreCase).Take(8).Select(g => (Owner: g.Key, Type: g.Select(r => r.OwnerType).FirstOrDefault(t => t is not null))).ToList();
        await Task.WhenAll(owners.Select(async x =>
        {
            bool have; lock (_gate) have = _billing.ContainsKey(x.Owner);
            if (have) return;
            var b = await ci.GetBillingUsageAsync(x.Owner, x.Type, now.Year, now.Month, ct);
            lock (_gate) { if (gen == _generation) _billing[x.Owner] = b; }
        }));
        if (gen == _generation) { Recompute(); Raise(); }
    }

    // ---------------------------------------------------------------- aggregation

    /// <summary>Earliest start among the billing periods in play: runs older than that cannot contribute to any current period.</summary>
    private static DateTimeOffset WindowStart(RunnerInsightsOptions o, DateTimeOffset now) =>
        o.Providers.Values.Select(p => BillingPeriod.For(now, p.CycleStartDay).Start).Append(BillingPeriod.For(now).Start).Min();

    private (List<JobSample> Samples, List<(RepositoryInfo Repo, WorkflowRunInfo Run)> Pending, UsageCoverage Coverage, Dictionary<string, UsageCoverage> ByOwner) Collect(
        IReadOnlyList<RepoSnapshot> snaps, Engine e, DateTimeOffset now, string ctx)
    {
        var since = WindowStart(e.Options, now);
        var samples = new List<JobSample>(); var pending = new List<(RepositoryInfo, WorkflowRunInfo)>();
        HashSet<RepoRef> failed; Dictionary<RepoRef, Listed> listed;
        lock (_gate) { failed = [.. _failed]; listed = new(_listed); }
        var scopedByOwner = overview.ScopedRepositories().GroupBy(r => r.Ref.Owner, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var acc = new Dictionary<string, (int Repos, int Eligible, int Measured, bool Trunc, int Failed)>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in snaps)
        {
            // The period listing is authoritative; until it has loaded, the Overview's recent-run window stands in and is reported as truncated when full.
            var hasList = listed.TryGetValue(s.Repo.Ref, out var l) && l.Since == since;
            var source = hasList ? l!.Period.Runs : s.Runs.Where(r => r.CreatedAt >= since).ToList();
            var runs = source.Where(r => r.State.IsCompleted()).OrderByDescending(r => r.SortTime).ToList();
            var trunc = hasList ? !l!.Period.Complete : s.Runs.Count >= overview.RunWindow && s.Runs.Count > 0 && s.Runs.Min(r => r.CreatedAt) >= since;
            var measured = 0;
            foreach (var run in runs)
            {
                if (store.TryGet(KeyOf(ctx, s.Repo.Ref, run), out var jobs)) { samples.AddRange(jobs); measured++; }
                else pending.Add((s.Repo, run));
            }
            var isFailed = failed.Contains(s.Repo.Ref);
            var owner = s.Repo.Ref.Owner; var a = acc.GetValueOrDefault(owner);
            acc[owner] = (a.Repos + (measured == runs.Count && !trunc && !isFailed && hasList ? 1 : 0), a.Eligible + runs.Count, a.Measured + measured, a.Trunc | trunc, a.Failed + (isFailed ? 1 : 0));
        }
        pending = pending.OrderByDescending(p => p.Item2.SortTime).ToList();
        var byOwner = acc.ToDictionary(kv => kv.Key, kv => new UsageCoverage(scopedByOwner.GetValueOrDefault(kv.Key, 0), kv.Value.Repos, kv.Value.Eligible, kv.Value.Measured, kv.Value.Trunc, kv.Value.Failed), StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, n) in scopedByOwner) if (!byOwner.ContainsKey(owner)) byOwner[owner] = new UsageCoverage(n, 0, 0, 0, false, 0);   // in scope, nothing listed yet
        var total = new UsageCoverage(scopedByOwner.Values.Sum(), byOwner.Values.Sum(c => c.RepositoriesMeasured), byOwner.Values.Sum(c => c.RunsEligible), byOwner.Values.Sum(c => c.RunsMeasured), byOwner.Values.Any(c => c.RunWindowTruncated), byOwner.Values.Sum(c => c.RepositoriesFailed));
        return (samples, pending, total, byOwner);
    }

    private void Recompute()
    {
        var e = EngineOf(); var now = time.GetUtcNow();
        var (samples, pending, coverage, byOwner) = Collect(overview.Snapshots(), e, now, Context);
        var inScope = overview.ScopedRepositories().Select(r => r.Ref.Owner).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, BillingUsage> billing; lock (_gate) billing = _billing.Where(b => inScope.Contains(b.Key)).ToDictionary(b => b.Key, b => b.Value, StringComparer.OrdinalIgnoreCase);   // an account outside the org scope is not part of this view
        var report = e.Aggregator.Build(e.Estimator.Estimate(samples, now), coverage, byOwner, billing, now);
        lock (_gate) { Report = report; PendingRuns = pending.Count; }
    }

    private void Raise() { Version++; Changed?.Invoke(); }

    // ---------------------------------------------------------------- one repository (repository page)

    /// <summary>
    /// Measures one repository on demand and reads its workflow files: configured runners (from <c>runs-on</c> and Actions variables) next to
    /// the runners that actually executed. Bounded: at most <see cref="WorkflowFileLimit"/> workflow files and one job request per unseen run
    /// (<c>more</c> raises the budget to the explicit-collection size).
    /// </summary>
    public const int WorkflowFileLimit = 40;

    public async Task<RepoRunnerView> LoadRepoAsync(RepoRef repo, bool force, CancellationToken ct, bool more = false)
    {
        lock (_gate) if (!force && !more && _repoViews.TryGetValue(repo, out var hit) && time.GetUtcNow() - hit.At < TimeSpan.FromMinutes(5)) return hit.View;
        var e = EngineOf(); var now = time.GetUtcNow(); var ctx = Context; int gen;
        lock (_gate) gen = _generation;
        var info = session.Repositories.FirstOrDefault(r => r.Ref.Equals(repo)) ?? new RepositoryInfo(repo, null, "main", true, "", null);
        var since = WindowStart(e.Options, now);
        var period = await ci.ListRunsSinceAsync(repo, since, Math.Max(100, e.Options.MaxRunsPerRepository), ct);
        var workflows = await ci.ListWorkflowsAsync(repo, ct);

        var eligible = period.Runs.Where(r => r.State.IsCompleted()).OrderByDescending(r => r.SortTime).ToList();
        var budget = new Budget(Math.Max(1, more ? e.Options.MaxJobRequestsPerCollect : e.Options.MaxJobRequestsPerRefresh));
        var failed = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(e.Options.Concurrency, 1, 8));
        await Task.WhenAll(eligible.Where(r => !store.TryGet(KeyOf(ctx, repo, r), out _)).Select(async r =>
        {
            await gate.WaitAsync(ct);
            try { if (RateOk(e.Options, out _) && budget.TryTake()) await FetchRunAsync(info, r, ctx, ct); }
            catch (CiProviderException ex) { Interlocked.Increment(ref failed); if (ex.StatusCode is 429 || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) budget.Exhaust(); }
            finally { gate.Release(); }
        }));

        var samples = new List<JobSample>(); var measured = 0;
        foreach (var r in eligible) if (store.TryGet(KeyOf(ctx, repo, r), out var j)) { samples.AddRange(j); measured++; }
        var coverage = new UsageCoverage(1, measured == eligible.Count && period.Complete && failed == 0 ? 1 : 0, eligible.Count, measured, !period.Complete, failed > 0 ? 1 : 0);
        var usage = e.Aggregator.Build(e.Estimator.Estimate(samples, now), coverage, new Dictionary<string, UsageCoverage> { [repo.Owner] = coverage }, new Dictionary<string, BillingUsage>(), now);

        var vars = await ci.GetActionsVariablesAsync(repo, ct);
        var (rows, read) = await ReadWorkflowsAsync(repo, workflows, usage, vars, ct);
        var view = new RepoRunnerView(repo, info.IsPrivate, usage, rows, vars, read, workflows.Count(w => w.IsActive), coverage, now);
        lock (_gate) { if (gen == _generation) _repoViews[repo] = (view, now); }
        return view;
    }

    public static bool IsDynamic(string path) => path.StartsWith("dynamic/", StringComparison.Ordinal);

    private async Task<(List<WorkflowRunnerRow>, int)> ReadWorkflowsAsync(RepoRef repo, IReadOnlyList<WorkflowInfo> workflows, RunnerUsageReport usage, ActionsVariables vars, CancellationToken ct)
    {
        var active = workflows.Where(w => w.IsActive).OrderBy(w => w.Path, StringComparer.Ordinal).Take(WorkflowFileLimit).ToList();
        var actual = usage.Providers.SelectMany(p => p.Workflows).ToList();
        IReadOnlyDictionary<string, string>? values = vars.IsAvailable ? vars.Values : null;
        using var gate = new SemaphoreSlim(4);
        var read = 0;

        async Task<string?> File(string path)
        {
            await gate.WaitAsync(ct);
            try { return await ci.GetFileAsync(repo, path, ct); }
            catch (CiProviderException) { return null; }
            finally { gate.Release(); }
        }

        var rows = await Task.WhenAll(active.Select(async w =>
        {
            // GitHub-managed dynamic workflows (Copilot, dependency graph, Dependabot) have no workflow file to read.
            var yaml = IsDynamic(w.Path) ? null : await File(w.Path);
            if (yaml is not null) Interlocked.Increment(ref read);
            var cfg = RunsOnResolver.Resolve(w.Path, yaml, values);
            var runners = cfg.Runners.ToList();
            foreach (var (job, path) in cfg.LocalReusable)                      // one level of local reusable workflows
            {
                var inner = RunsOnResolver.Resolve(path, await File(path), values);
                runners.AddRange(inner.Runners.Select(r => r with { JobId = job + " › " + r.JobId, JobName = job + " › " + r.JobName }));
                if (inner.ParseFailed) runners.Add(new ConfiguredRunner(job, job, [], RunnerRole.Unknown, "reusable workflow", $"{path} could not be read."));
            }
            return new WorkflowRunnerRow(w, runners, cfg.ParseFailed && cfg.LocalReusable.Count == 0 && !IsDynamic(w.Path), false, actual.Where(a => a.WorkflowId == w.Id && a.Repo.Equals(repo)).ToList());
        }));

        // Workflows that only appear in history (deleted, renamed or disabled since) still show what ran.
        var known = rows.Select(r => r.Workflow.Id).ToHashSet();
        var historical = actual.Where(a => a.Repo.Equals(repo) && !known.Contains(a.WorkflowId)).GroupBy(a => a.WorkflowId)
            .Select(g => new WorkflowRunnerRow(new WorkflowInfo(g.Key, g.First().WorkflowName, g.First().WorkflowPath, false, null), [], false, true, g.ToList()));
        return ([.. rows, .. historical], read);
    }
}
