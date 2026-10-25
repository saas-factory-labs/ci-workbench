using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Demo;

/// <summary>
/// Fully synthetic, deterministic provider used for demo mode and tests. All names, people and logs are invented.
/// Timestamps are relative to the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class DemoCiProvider : ICiProvider
{
    public const string MonorepoName = "platform-monorepo";
    public const string ActorRepoName = "review-scraper-actor";
    public const string Owner = "example-org";
    public static readonly RepoRef Monorepo = new(Owner, MonorepoName);
    public static readonly RepoRef ActorRepo = new(Owner, ActorRepoName);

    private readonly DateTimeOffset _now;
    private readonly Dictionary<RepoRef, RepositoryInfo> _repos = [];
    private readonly Dictionary<RepoRef, List<WorkflowInfo>> _workflows = [];
    private readonly Dictionary<RepoRef, List<WorkflowRunInfo>> _runs = [];
    private readonly Dictionary<long, List<JobInfo>> _jobs = [];
    private readonly Dictionary<long, string> _logs = [];
    private readonly Dictionary<long, List<JobInfo>> _priorAttempts = [];
    private readonly Dictionary<(RepoRef, string), string> _files = [];

    public DemoCiProvider(TimeProvider? time = null)
    {
        _now = (time ?? TimeProvider.System).GetUtcNow();
        BuildMonorepo();
        BuildActorRepo();
        BuildFillerRepos();
    }

    public string Name => "Demo data";
    public bool IsDemo => true;

    public async IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var all = _repos.Values.OrderByDescending(r => r.PushedAt).ToList();
        const int pageSize = 10;
        var pages = (int)Math.Ceiling(all.Count / (double)pageSize);
        for (var p = 0; p < pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(30, ct);
            yield return new RepositoryPage(all.Skip(p * pageSize).Take(pageSize).ToList(), p + 1, pages, p == pages - 1);
        }
    }

    public Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkflowInfo>>(_workflows.TryGetValue(repo, out var w) ? w : []);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkflowRunInfo>>(_runs.TryGetValue(repo, out var r) ? r.OrderByDescending(x => x.SortTime).Take(max).ToList() : []);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkflowRunInfo>>(_runs.TryGetValue(repo, out var r) ? r.Where(x => x.WorkflowId == workflowId).OrderByDescending(x => x.SortTime).Take(max).ToList() : []);

    public Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(repo, out var r) ? r.FirstOrDefault(x => x.Id == runId) : null);

    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JobInfo>>(_jobs.TryGetValue(runId, out var j) ? j : []);

    public Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct) =>
        Task.FromResult(_logs.TryGetValue(jobId, out var l) ? (l.Length > maxBytes ? l[^maxBytes..] : l) : null);

    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue((repo, path), out var c) ? c : null);

    public Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct) =>
        Task.FromResult<RateLimitInfo?>(new RateLimitInfo(5000, 4987, _now.AddMinutes(41)));

    public Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct) =>
        Task.FromResult(new ConnectionReport("demo-user (synthetic)",
        [
            new ConnectionCheck("Authentication", CheckStatus.Ok, "Demo mode: no credentials are used."),
            new ConnectionCheck("Repository discovery", CheckStatus.Ok, $"{_repos.Count} synthetic repositories."),
            new ConnectionCheck("Actions read", CheckStatus.Ok, "Synthetic workflows and runs."),
            new ConnectionCheck("Contents read", CheckStatus.Ok, "Synthetic configuration files."),
        ]));

    // ---------------------------------------------------------------- fixture A

    /// <summary>File name (without extension), display name, group assigned by YAML comment (null = assigned by central config).</summary>
    private static readonly (string File, string Name, string? Comment)[] MonorepoWorkflows =
    [
        // Build & Test (8)
        ("ci-api", "CI: API", null), ("ci-web", "CI: Web", null), ("ci-worker", "CI: Worker", null), ("ci-cli", "CI: CLI", null),
        ("ci-shared", "CI: Shared libraries", null), ("unit-tests", "Unit tests", null), ("integration-tests", "Integration tests", null), ("e2e-tests", "End-to-end tests", null),
        // Security (6, by YAML comment)
        ("codeql", "CodeQL analysis", "Security"), ("dependency-review", "Dependency review", "Security"), ("secret-scan", "Secret scan", "Security"),
        ("container-scan", "Container image scan", "Security"), ("license-audit", "License audit", "Security"), ("sbom", "SBOM generation", "Security"),
        // Deployment (5)
        ("deploy-staging", "Deploy: staging", null), ("deploy-production", "Deploy: production", null), ("deploy-preview", "Deploy: PR preview", null),
        ("rollback", "Rollback", null), ("migrate-db", "Database migrations", null),
        // Quality (5)
        ("lint", "Lint", null), ("format-check", "Format check", null), ("type-check", "Type check", null), ("coverage-report", "Coverage report", null), ("benchmarks", "Benchmarks", null),
        // Packages (5)
        ("publish-nuget", "Publish: NuGet", null), ("publish-npm", "Publish: npm", null), ("publish-images", "Publish: container images", null),
        ("publish-cli", "Publish: CLI binaries", null), ("nightly", "Nightly build", null),
        // Documentation (3)
        ("ci-docs", "CI: Docs", null), ("deploy-docs", "Deploy: docs site", null), ("link-check", "Link checker", null),
        // Maintenance (4)
        ("stale", "Close stale issues", null), ("labeler", "PR labeler", null), ("release-notes", "Release notes", null), ("cache-cleanup", "Cache cleanup", null),
        // AI Agents (3, by YAML comment)
        ("agent-triage", "Agent: issue triage", "AI Agents"), ("agent-review", "Agent: PR review", "AI Agents"), ("agent-docs", "Agent: docs sync", "AI Agents"),
    ];

    /// <summary>Workflows whose recent runs fail with a stable, recurring error: file -> (job, failing step, error kind, consecutive failures).</summary>
    private static readonly Dictionary<string, (string Job, string Step, string Kind, int Failures)> Recurring = new()
    {
        ["container-scan"] = ("Scan published images", "Trivy scan", "scan", 5),
        ["publish-images"] = ("Image rollout stabilization", "Wait for rollout", "rollout", 3),
        ["link-check"] = ("Check documentation links", "Run link checker", "link", 4),
    };

    private static readonly string[] Actors = ["github-actions", "river-dev", "sam-test", "jules-ops", "octo-bot"];
    private static readonly string[] Branches = ["main", "main", "main", "feat/pagination", "fix/cache-key", "chore/deps"];
    private static readonly string[] FlakyWords = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india"];

    private void BuildMonorepo()
    {
        var repo = Monorepo;
        _repos[repo] = new RepositoryInfo(repo, $"Synthetic large monorepo with {MonorepoWorkflows.Length} workflows (demo data).", "main", true, $"https://example.invalid/{repo.FullName}", _now.AddMinutes(-12), "Organization");
        var wfs = new List<WorkflowInfo>(); var runs = new List<WorkflowRunInfo>();
        for (var i = 0; i < MonorepoWorkflows.Length; i++)
        {
            var (file, name, _) = MonorepoWorkflows[i];
            var path = $".github/workflows/{file}.yml";
            var wf = new WorkflowInfo(1000 + i, name, path, i % 17 != 16, $"https://example.invalid/{repo.FullName}/actions/workflows/{file}.yml");
            wfs.Add(wf);
            var scheduled = file is "codeql" or "secret-scan" or "container-scan" or "nightly" or "stale" or "cache-cleanup" or "license-audit" or "link-check";
            Recurring.TryGetValue(file, out var rec);
            for (var n = 0; n < (rec.Failures > 0 ? 10 : 6); n++)
            {
                var id = 1_000_000 + i * 100 + n;
                var state = n == 0 && (file is "ci-web" or "deploy-staging") ? RunState.InProgress
                    : n == 1 && file == "ci-api" ? RunState.Queued
                    : rec.Failures > n ? RunState.Failure
                    : rec.Failures > 0 ? RunState.Success
                    : (i * 7 + n * 3) % 11 == 0 ? RunState.Failure
                    : (i + n) % 13 == 0 ? RunState.Cancelled : RunState.Success;
                var age = TimeSpan.FromHours(n * (scheduled ? 24 : 5) + 1 + i % 4);
                var started = _now - age;
                var branch = scheduled ? "main" : Branches[(i + n) % Branches.Length];
                var ev = scheduled ? "schedule" : branch == "main" ? "push" : "pull_request";
                var dur = TimeSpan.FromSeconds(90 + (i * 37 + n * 11) % 600);
                var rerun = state.IsCompleted() && ((file == "e2e-tests" && n == 2) || (file == "ci-api" && n == 3));
                var run = new WorkflowRunInfo(id, wf.Id, path, name, 200 + n, rerun ? 2 : 1, state, ev, branch, Sha(id), $"Demo commit message {id % 97}", Actors[(i + n) % Actors.Length],
                    started, started, state.IsActive() ? started.AddMinutes(1) : started + dur, $"https://example.invalid/{repo.FullName}/actions/runs/{id}");
                runs.Add(run);
                var jobName = rec.Job ?? "build";
                var steps = rec.Step is null ? new[] { "Checkout", "Setup toolchain", "Restore", "Build", "Test" } : new[] { "Checkout", "Setup toolchain", rec.Step };
                var kind = RunnerKinds.GetValueOrDefault(file, "trusted");
                var labels = ActualLabels(kind, n);
                var jobs = new List<JobInfo> { MakeJob(id, 0, jobName, state, started, dur, steps, labels) };
                if (file is "e2e-tests" or "unit-tests") jobs.Add(MakeJob(id, 1, jobName + " (firefox)", state == RunState.Failure ? RunState.Success : state, started, dur - TimeSpan.FromSeconds(20), steps, labels));   // matrix leg: a distinct job id
                _jobs[id] = jobs;
                if (rerun) _priorAttempts[id] = [MakeJob(id, 5, jobName, RunState.Failure, started - dur - TimeSpan.FromMinutes(12), dur, steps, labels, attempt: 1)];
                if (state == RunState.Failure)
                    _logs[id * 10] = LogFor(rec.Kind ?? "flaky", id);
            }
        }
        _workflows[repo] = wfs; _runs[repo] = runs;
        _files[(repo, ".ciworkbench.yml")] = """
            groups:
              - name: Build & Test
                description: Compile, unit, integration and end-to-end tests
                workflows: [".github/workflows/ci-api.yml", ".github/workflows/ci-web.yml", ".github/workflows/ci-worker.yml", ".github/workflows/ci-cli.yml", ".github/workflows/ci-shared.yml", ".github/workflows/*-tests.yml"]
              - name: Security
                description: Code, dependency and image scanning
                workflows: []
              - name: Deployment
                description: Staging and production delivery
                workflows: [".github/workflows/deploy-staging.yml", ".github/workflows/deploy-production.yml", ".github/workflows/deploy-preview.yml", ".github/workflows/rollback.yml", ".github/workflows/migrate-db.yml"]
              - name: Quality
                description: Linting, type checking and performance
                workflows: [".github/workflows/lint.yml", ".github/workflows/format-check.yml", ".github/workflows/type-check.yml", ".github/workflows/coverage-report.yml", ".github/workflows/benchmarks.yml"]
              - name: Packages
                description: Build and publish packages and images
                workflows: [".github/workflows/publish-*.yml", ".github/workflows/nightly.yml"]
              - name: Documentation
                description: Build, check and publish documentation
                workflows: [".github/workflows/ci-docs.yml", ".github/workflows/deploy-docs.yml", ".github/workflows/link-check.yml"]
              - name: Maintenance
                description: Housekeeping automation
                workflows: [".github/workflows/stale.yml", ".github/workflows/labeler.yml", ".github/workflows/release-notes.yml", ".github/workflows/cache-cleanup.yml"]
              - name: AI Agents
                description: Automation driven by coding agents
                workflows: []
            """;
        foreach (var (file, _, comment) in MonorepoWorkflows)
            _files[(repo, $".github/workflows/{file}.yml")] = (comment is null ? "" : $"# ci-workbench-group: {comment}\n")
                + $"name: demo\non: push\njobs:\n  build:\n    runs-on: {RunsOnFor(RunnerKinds.GetValueOrDefault(file, "trusted"))}\n    steps:\n      - run: echo demo\n";
    }

    // ---------------------------------------------------------------- fixture B

    private void BuildActorRepo()
    {
        var repo = ActorRepo;
        _repos[repo] = new RepositoryInfo(repo, "Daily CI health checks and production validation for a synthetic review scraper (demo data).", "main", true, $"https://example.invalid/{repo.FullName}", _now.AddHours(-2), "Organization");
        var ci = new WorkflowInfo(2001, "CI", ".github/workflows/ci.yml", true, null);
        var quality = new WorkflowInfo(2002, "Code Quality", ".github/workflows/quality.yml", true, null);
        _workflows[repo] = [ci, quality];
        var runs = new List<WorkflowRunInfo>();
        for (var n = 0; n < 28; n++)
        {
            var id = 2_000_000 + n;
            var started = _now.Date.AddHours(10).AddDays(-n / 1.0).AddMinutes(2);
            if (started > _now) started = started.AddDays(-1);
            // n=0 is newest. Deep paging has failed for 15 runs in a row; the browser dependency for 9 runs, then one
            // different assertion failure in the same job (must NOT merge with the browser series).
            var pagingFail = n < 15;
            var scenario = n == 9 ? "assertion" : n <= 10 ? "browser" : "pass";
            var failed = pagingFail || scenario != "pass";
            var dur = TimeSpan.FromSeconds(600 + n * 7);
            runs.Add(new WorkflowRunInfo(id, ci.Id, ci.Path, "CI", 400 - n, 1, failed ? RunState.Failure : RunState.Success, n % 5 == 4 ? "workflow_dispatch" : "schedule", "main", Sha(id),
                "Demo: scheduled health check", "github-actions", started, started, started + dur, $"https://example.invalid/{repo.FullName}/actions/runs/{id}"));
            _jobs[id] =
            [
                MakeJob(id, 0, "TypeScript Build", RunState.Success, started, TimeSpan.FromSeconds(134), ["Checkout", "Install", "tsc"], Gh),
                MakeJob(id, 1, "Jest core tests", RunState.Success, started, TimeSpan.FromSeconds(262), ["Checkout", "Install", "Run jest"], Gh),
                MakeJob(id, 2, "Production Image Smoke Test", RunState.Success, started, TimeSpan.FromSeconds(68), ["Pull image", "Run container smoke test"], Gh),
                MakeJob(id, 3, "Deep-paging test", pagingFail ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(300), ["Checkout", "Install", "Run deep-paging pages 10-15"], Gh),
                MakeJob(id, 4, "Browser scenario tests", scenario != "pass" ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(240), ["Checkout", "Install browsers", "Run scenarios"], ["tenki-standard-small-2c-4g"]),
            ];
            if (pagingFail) _logs[id * 10 + 3] = LogFor("paging", id);
            if (scenario != "pass") _logs[id * 10 + 4] = LogFor(scenario, id);
        }
        // Code Quality: healthy, six runs
        for (var n = 0; n < 6; n++)
        {
            var id = 2_100_000 + n; var started = _now.Date.AddHours(6).AddDays(-n);
            if (started > _now) started = started.AddDays(-1);
            runs.Add(new WorkflowRunInfo(id, quality.Id, quality.Path, "Code Quality", 90 - n, 1, RunState.Success, n % 2 == 0 ? "push" : "pull_request", n % 2 == 0 ? "main" : "feat/selectors", Sha(id), "Demo: lint and type check",
                n % 2 == 0 ? "river-dev" : "sam-test", started, started, started.AddSeconds(95), $"https://example.invalid/{repo.FullName}/actions/runs/{id}"));
            _jobs[id] =
            [
                MakeJob(id, 0, "Lint", RunState.Success, started, TimeSpan.FromSeconds(41), ["Checkout", "Install", "eslint"], Gh),
                MakeJob(id, 1, "Type check", RunState.Success, started, TimeSpan.FromSeconds(54), ["Checkout", "Install", "tsc --noEmit"], Gh),
            ];
        }
        _runs[repo] = runs;
        _files[(repo, GroupConfigContract.PreferredPath)] = """
            groups:
              - name: CI
                description: Build, test and publish the actor
                workflows: [".github/workflows/ci.yml"]
              - name: Code Quality
                description: Linting, type checking and code quality analysis
                workflows: [".github/workflows/quality.yml"]
            """;
    }

    private void BuildFillerRepos()
    {
        string[] names = ["billing-service", "design-system", "infra-modules", "mobile-app", "data-pipeline", "docs-site", "sdk-dotnet", "sdk-typescript", "auth-gateway", "playground", "cli-tools", "ml-experiments"];
        for (var i = 0; i < names.Length; i++)
        {
            var repo = new RepoRef(i % 3 == 0 ? "example-labs" : Owner, names[i]);
            _repos[repo] = new RepositoryInfo(repo, $"Synthetic repository {names[i]} (demo data).", "main", i % 4 == 0, $"https://example.invalid/{repo.FullName}", _now.AddDays(-1 - i), "Organization");
            _workflows[repo] = [new WorkflowInfo(3000 + i, "CI", ".github/workflows/ci.yml", true, null)];
            _runs[repo] = [];
        }
        BuildFillerHistory(new RepoRef("example-labs", "billing-service"), 3000, [["ubuntu-latest"], ["windows-latest"], ["self-hosted", "linux", "x64"], ["macos-latest"]]);
        BuildFillerHistory(new RepoRef("example-labs", "mobile-app"), 3003, [["ubuntu-latest"], ["macos-latest"]]);   // a public repository: standard runners are free
    }

    /// <summary>A few completed runs on a mix of runner labels, in the current month, for the account that has no billing permission.</summary>
    private void BuildFillerHistory(RepoRef repo, long workflowId, string[][] labelSets)
    {
        var wf = _workflows[repo][0];
        var runs = new List<WorkflowRunInfo>();
        for (var n = 0; n < 8; n++)
        {
            var id = 3_000_000 + workflowId * 10 + n;
            var started = _now.AddHours(-(n * 7 + 2));
            var dur = TimeSpan.FromSeconds(180 + n * 41);
            runs.Add(new WorkflowRunInfo(id, wf.Id, wf.Path, "CI", 50 + n, 1, RunState.Success, "push", "main", Sha(id), "Demo commit", "river-dev", started, started, started + dur, $"https://example.invalid/{repo.FullName}/actions/runs/{id}"));
            _jobs[id] = labelSets.Select((l, k) => MakeJob(id, k, $"build ({string.Join(' ', l)})", RunState.Success, started, dur + TimeSpan.FromSeconds(k * 30), ["Checkout", "Build"], l)).ToList();
        }
        _runs[repo] = runs;
    }

    // ---------------------------------------------------------------- runner insights

    private static readonly string[] Gh = ["ubuntu-latest"];

    /// <summary>Which runner variable (or literal label) each monorepo workflow is configured with.</summary>
    private static readonly Dictionary<string, string> RunnerKinds = new()
    {
        ["ci-api"] = "default", ["ci-web"] = "default", ["ci-worker"] = "default", ["ci-cli"] = "default", ["ci-shared"] = "default", ["ci-docs"] = "default",
        ["lint"] = "default", ["format-check"] = "default", ["type-check"] = "default", ["unit-tests"] = "default",
        ["integration-tests"] = "fast", ["coverage-report"] = "fast", ["e2e-tests"] = "lit:bp-ubuntu-latest-4-vcpu", ["benchmarks"] = "lit:blacksmith-4vcpu-ubuntu-2404",
        ["codeql"] = "heavy", ["dependency-review"] = "heavy", ["secret-scan"] = "heavy", ["container-scan"] = "heavy", ["license-audit"] = "heavy", ["sbom"] = "heavy", ["link-check"] = "heavy", ["nightly"] = "heavy",
        ["agent-triage"] = "lit:celesto-cloud", ["agent-review"] = "lit:celesto-cloud", ["agent-docs"] = "lit:celesto-cloud",
    };

    private static string RunsOnFor(string kind) => kind switch
    {
        "default" => "${{ fromJSON(vars.CI_RUNNER_DEFAULT || '[\"tenki-standard-small-2c-4g\"]') }}",
        "fast" => "${{ fromJSON(vars.CI_RUNNER_FAST || '[\"avrea-ubuntu-latest-4-vcpu\"]') }}",
        "heavy" => "${{ fromJSON(vars.CI_RUNNER_HEAVY || '[\"hetzner-arc\"]') }}",
        "trusted" => "${{ fromJSON(vars.CI_RUNNER_TRUSTED || '[\"ubuntu-latest\"]') }}",
        _ => kind[4..],
    };

    /// <summary>
    /// What really ran. The demo router moved DEFAULT from Tenki to Avrea two runs ago, so the configured variable (Avrea) and the
    /// history (mostly Tenki) differ on purpose: that is the "configured vs actually executed" case.
    /// </summary>
    private static string[] ActualLabels(string kind, int n) => kind switch
    {
        "default" => n < 2 ? ["avrea-ubuntu-latest"] : ["tenki-standard-small-2c-4g"],
        "fast" => ["avrea-ubuntu-latest-4-vcpu"],
        "heavy" => ["hetzner-arc"],
        "trusted" => ["ubuntu-latest"],
        _ => [kind[4..]],
    };

    public Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct)
    {
        var latest = _jobs.TryGetValue(runId, out var j) ? j : [];
        return Task.FromResult<IReadOnlyList<JobInfo>>(_priorAttempts.TryGetValue(runId, out var prior) ? [.. prior, .. latest] : latest);
    }

    /// <summary>The organisation the demo token may read variables for; the second account models a token without the Variables permission.</summary>
    public Task<ActionsVariables> GetActionsVariablesAsync(RepoRef repo, CancellationToken ct) =>
        Task.FromResult(repo.Owner == Owner
            ? new ActionsVariables(VariablesAccess.Available, new Dictionary<string, string>
            {
                ["CI_RUNNER_DEFAULT"] = "[\"avrea-ubuntu-latest\"]", ["CI_RUNNER_FAST"] = "[\"avrea-ubuntu-latest-4-vcpu\"]",
                ["CI_RUNNER_HEAVY"] = "[\"hetzner-arc\"]", ["CI_RUNNER_TRUSTED"] = "[\"ubuntu-latest\"]",
            }, null)
            : new ActionsVariables(VariablesAccess.PermissionMissing, new Dictionary<string, string>(), "Reading Actions variables needs the fine-grained permission Variables: Read-only (repository and, for shared variables, organization)."));

    /// <summary>Verified billing for the demo organisation (its free minutes are used up); none for the other account (no billing permission).</summary>
    public Task<BillingUsage> GetBillingUsageAsync(string owner, string? ownerType, int year, int month, CancellationToken ct) =>
        Task.FromResult(owner == Owner
            ? new BillingUsage(BillingAccess.Available,
            [
                new BillingLine("Actions Linux", "Minutes", 2630, 0.006, 15.78, 12.00, 3.78),
                new BillingLine("Actions macOS 3-core", "Minutes", 85, 0.062, 5.27, 5.27, 0),
                new BillingLine("Actions storage", "GigabyteHours", 75.8, 0.00033602, 0.0255, 0.0255, 0),
            ], null, year, month, _now)
            : new BillingUsage(BillingAccess.PermissionMissing, [], $"Billing usage needs the fine-grained organization permission Administration: Read-only (and an organization owner or billing manager). Usage is estimated from job history instead.", year, month, null));

    // ---------------------------------------------------------------- helpers

    private static string Sha(long seed) => (seed * 2654435761L % 0xFFFFFFFFFL).ToString("x9");

    private JobInfo MakeJob(long runId, int k, string name, RunState state, DateTimeOffset started, TimeSpan dur, string[] steps, string[]? labels = null, int attempt = 1)
    {
        var failIdx = state == RunState.Failure ? steps.Length - 1 : -1;
        var list = steps.Select((s, idx) => new StepInfo(idx + 1, s,
            state.IsActive() ? (idx == 0 ? RunState.InProgress : RunState.Queued)
            : state == RunState.Cancelled ? (idx < steps.Length - 1 ? RunState.Success : RunState.Cancelled)
            : failIdx >= 0 ? (idx < failIdx ? RunState.Success : RunState.Failure) : RunState.Success)).ToList();
        labels ??= Gh;
        var hosted = labels.Any(l => l.StartsWith("ubuntu-", StringComparison.Ordinal) || l.StartsWith("windows-", StringComparison.Ordinal) || l.StartsWith("macos-", StringComparison.Ordinal)) && !labels.Contains("self-hosted");
        return new JobInfo(runId * 10 + k, name, state, state == RunState.Queued ? null : started, state.IsActive() ? null : started + dur, list, $"https://example.invalid/jobs/{runId * 10 + k}",
            labels, hosted ? $"GitHub Actions {runId % 90 + 1}" : $"{labels[0]}-{runId % 7}", hosted ? "GitHub Actions" : "Default", attempt, started.AddSeconds(-12));
    }

    private string LogFor(string kind, long runId)
    {
        var ts = _now.AddMinutes(-(runId % 500)).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var sha = Sha(runId);
        var ws = "/home/runner/work/synthetic/synthetic";
        var err = kind switch
        {
            "paging" => "TimeoutError: page.goto: Timeout 30000ms exceeded while loading reviews page 12" + $" (request {new Guid((int)runId, 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]):D})",
            "browser" => "Error: browserType.launch: Executable doesn't exist at " + ws + "/.cache/camoufox/firefox (Firefox/Camoufox environment)",
            "assertion" => $"AssertionError: expected {3 + runId % 4} reviews but received 0",
            "scan" => "error TRIVY0001: HIGH vulnerability CVE-2026-1234 in libssl3 (container image sha256:" + sha + ")",
            "rollout" => "Error: deployment \"web\" exceeded its progress deadline waiting for 2 replicas to become ready",
            "link" => $"Error: 3 broken links found in docs (see {ws}/docs/index.md)",
            _ => $"Error: unexpected {FlakyWords[(int)(runId % FlakyWords.Length)]} failure in step",
        };
        return $"{ts} ##[group]Run tests\n{ts} noise line {runId}\n{ts} ##[endgroup]\n{ts} ##[error]{err}\n{ts} ##[error]Process completed with exit code 1.\n";
    }
}
