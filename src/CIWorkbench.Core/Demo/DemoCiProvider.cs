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
        _repos[repo] = new RepositoryInfo(repo, $"Synthetic large monorepo with {MonorepoWorkflows.Length} workflows (demo data).", "main", false, $"https://example.invalid/{repo.FullName}", _now.AddMinutes(-12));
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
                var run = new WorkflowRunInfo(id, wf.Id, path, name, 200 + n, 1, state, ev, branch, Sha(id), $"Demo commit message {id % 97}", Actors[(i + n) % Actors.Length],
                    started, started, state.IsActive() ? started.AddMinutes(1) : started + dur, $"https://example.invalid/{repo.FullName}/actions/runs/{id}");
                runs.Add(run);
                var jobName = rec.Job ?? "build";
                var steps = rec.Step is null ? new[] { "Checkout", "Setup toolchain", "Restore", "Build", "Test" } : new[] { "Checkout", "Setup toolchain", rec.Step };
                _jobs[id] = [MakeJob(id, 0, jobName, state, started, dur, steps)];
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
        foreach (var (file, _, comment) in MonorepoWorkflows.Where(w => w.Comment is not null))
            _files[(repo, $".github/workflows/{file}.yml")] = $"# ci-workbench-group: {comment}\nname: demo\non: push\n";
    }

    // ---------------------------------------------------------------- fixture B

    private void BuildActorRepo()
    {
        var repo = ActorRepo;
        _repos[repo] = new RepositoryInfo(repo, "Daily CI health checks and production validation for a synthetic review scraper (demo data).", "main", false, $"https://example.invalid/{repo.FullName}", _now.AddHours(-2));
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
                MakeJob(id, 0, "TypeScript Build", RunState.Success, started, TimeSpan.FromSeconds(134), ["Checkout", "Install", "tsc"]),
                MakeJob(id, 1, "Jest core tests", RunState.Success, started, TimeSpan.FromSeconds(262), ["Checkout", "Install", "Run jest"]),
                MakeJob(id, 2, "Production Image Smoke Test", RunState.Success, started, TimeSpan.FromSeconds(68), ["Pull image", "Run container smoke test"]),
                MakeJob(id, 3, "Deep-paging test", pagingFail ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(300), ["Checkout", "Install", "Run deep-paging pages 10-15"]),
                MakeJob(id, 4, "Browser scenario tests", scenario != "pass" ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(240), ["Checkout", "Install browsers", "Run scenarios"]),
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
                MakeJob(id, 0, "Lint", RunState.Success, started, TimeSpan.FromSeconds(41), ["Checkout", "Install", "eslint"]),
                MakeJob(id, 1, "Type check", RunState.Success, started, TimeSpan.FromSeconds(54), ["Checkout", "Install", "tsc --noEmit"]),
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
            _repos[repo] = new RepositoryInfo(repo, $"Synthetic repository {names[i]} (demo data).", "main", i % 4 == 0, $"https://example.invalid/{repo.FullName}", _now.AddDays(-1 - i));
            _workflows[repo] = [new WorkflowInfo(3000 + i, "CI", ".github/workflows/ci.yml", true, null)];
            _runs[repo] = [];
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string Sha(long seed) => (seed * 2654435761L % 0xFFFFFFFFFL).ToString("x9");

    private JobInfo MakeJob(long runId, int k, string name, RunState state, DateTimeOffset started, TimeSpan dur, string[] steps)
    {
        var failIdx = state == RunState.Failure ? steps.Length - 1 : -1;
        var list = steps.Select((s, idx) => new StepInfo(idx + 1, s,
            state.IsActive() ? (idx == 0 ? RunState.InProgress : RunState.Queued)
            : state == RunState.Cancelled ? (idx < steps.Length - 1 ? RunState.Success : RunState.Cancelled)
            : failIdx >= 0 ? (idx < failIdx ? RunState.Success : RunState.Failure) : RunState.Success)).ToList();
        return new JobInfo(runId * 10 + k, name, state, started, state.IsActive() ? null : started + dur, list, $"https://example.invalid/jobs/{runId * 10 + k}");
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
