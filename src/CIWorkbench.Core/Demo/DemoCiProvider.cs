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

    private static readonly (string File, string Name, string? Comment)[] MonorepoWorkflows =
    [
        ("ci-api", "CI: API", null), ("ci-web", "CI: Web", null), ("ci-worker", "CI: Worker", null), ("ci-cli", "CI: CLI", null),
        ("ci-docs", "CI: Docs", null), ("ci-shared", "CI: Shared libraries", null), ("unit-tests", "Unit tests", null),
        ("integration-tests", "Integration tests", null), ("e2e-tests", "End-to-end tests", null), ("lint", "Lint", null),
        ("format-check", "Format check", null), ("codeql", "CodeQL analysis", "Security"), ("dependency-review", "Dependency review", "Security"),
        ("secret-scan", "Secret scan", "Security"), ("container-scan", "Container image scan", "Security"), ("license-audit", "License audit", "Security"),
        ("sbom", "SBOM generation", "Security"), ("deploy-staging", "Deploy: staging", null), ("deploy-production", "Deploy: production", null),
        ("deploy-docs", "Deploy: docs site", null), ("rollback", "Rollback", null), ("migrate-db", "Database migrations", null),
        ("agent-triage", "Agent: issue triage", "AI Agents"), ("agent-review", "Agent: PR review", "AI Agents"), ("agent-docs", "Agent: docs sync", "AI Agents"),
        ("agent-flaky", "Agent: flaky test hunter", "AI Agents"), ("publish-nuget", "Publish: NuGet", null), ("publish-npm", "Publish: npm", null),
        ("publish-images", "Publish: container images", null), ("publish-cli", "Publish: CLI binaries", null), ("nightly", "Nightly build", null),
        ("stale", "Close stale issues", "Maintenance"), ("labeler", "PR labeler", "Maintenance"), ("release-notes", "Release notes", "Maintenance"),
        ("cache-cleanup", "Cache cleanup", "Maintenance"), ("dependabot-auto", "Dependabot auto-merge", "Maintenance"),
        ("benchmarks", "Benchmarks", null), ("link-check", "Link checker", null), ("pages", "pages-build-deployment", null),
    ];

    private static readonly string[] Actors = ["octo-bot", "github-actions", "river-dev", "sam-test", "jules-ops"];
    private static readonly string[] Branches = ["main", "main", "main", "feat/pagination", "fix/cache-key", "chore/deps"];

    private void BuildMonorepo()
    {
        var repo = Monorepo;
        _repos[repo] = new RepositoryInfo(repo, "Synthetic large monorepo with 39 workflows (demo data).", "main", false, $"https://example.invalid/{repo.FullName}", _now.AddMinutes(-12));
        var wfs = new List<WorkflowInfo>(); var runs = new List<WorkflowRunInfo>();
        for (var i = 0; i < MonorepoWorkflows.Length; i++)
        {
            var (file, name, _) = MonorepoWorkflows[i];
            var path = file == "pages" ? "dynamic/pages/pages-build-deployment" : $".github/workflows/{file}.yml";
            var wf = new WorkflowInfo(1000 + i, name, path, i % 17 != 16, $"https://example.invalid/{repo.FullName}/actions/workflows/{file}.yml");
            wfs.Add(wf);
            var scheduled = file is "codeql" or "secret-scan" or "container-scan" or "nightly" or "stale" or "cache-cleanup" or "license-audit" or "link-check";
            var failing = file is "container-scan" or "e2e-tests" or "link-check";
            for (var n = 0; n < 10; n++)
            {
                var id = 1_000_000 + i * 100 + n;
                var state = n == 0 && file is "ci-web" or "deploy-staging" ? RunState.InProgress
                    : n == 1 && file == "ci-api" ? RunState.Queued
                    : failing && n < 5 ? RunState.Failure
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
                _jobs[id] = [MakeJob(id, 0, "build", state, started, dur, ["Checkout", "Setup toolchain", "Restore", "Build", "Test"], file)];
                if (state == RunState.Failure)
                    _logs[id * 10] = LogFor(file == "container-scan" ? "scan" : file == "link-check" ? "link" : file == "e2e-tests" ? "e2e" : "generic", id, 0);
            }
        }
        _workflows[repo] = wfs; _runs[repo] = runs;
        _files[(repo, ".ciworkbench.yml")] = """
            groups:
              - name: Build & Test
                workflows: [".github/workflows/ci-*.yml", ".github/workflows/*-tests.yml", ".github/workflows/lint.yml", ".github/workflows/format-check.yml"]
              - name: Deployment
                description: Staging and production delivery
                workflows: [".github/workflows/deploy-*.yml", ".github/workflows/rollback.yml", ".github/workflows/migrate-db.yml"]
              - name: Packages
                workflows: [".github/workflows/publish-*.yml", ".github/workflows/nightly.yml"]
            """;
        foreach (var (file, _, comment) in MonorepoWorkflows.Where(w => w.Comment is not null))
            _files[(repo, $".github/workflows/{file}.yml")] = $"# ci-workbench-group: {comment}\nname: demo\non: push\n";
    }

    // ---------------------------------------------------------------- fixture B

    private void BuildActorRepo()
    {
        var repo = ActorRepo;
        _repos[repo] = new RepositoryInfo(repo, "Synthetic scraper actor with two workflows and recurring failures (demo data).", "main", false, $"https://example.invalid/{repo.FullName}", _now.AddHours(-2));
        var ci = new WorkflowInfo(2001, "CI", ".github/workflows/ci.yml", true, null);
        var smoke = new WorkflowInfo(2002, "Production smoke test", ".github/workflows/smoke.yml", true, null);
        _workflows[repo] = [ci, smoke];
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
                MakeJob(id, 0, "typescript-build", RunState.Success, started, TimeSpan.FromSeconds(134), ["Checkout", "Install", "tsc"], "ci"),
                MakeJob(id, 1, "jest-core-tests", RunState.Success, started, TimeSpan.FromSeconds(262), ["Checkout", "Install", "Run jest"], "ci"),
                MakeJob(id, 2, "docker-image-smoke", RunState.Success, started, TimeSpan.FromSeconds(68), ["Build image", "Run container smoke test"], "ci"),
                MakeJob(id, 3, "deep-paging-test", pagingFail ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(300), ["Checkout", "Install", "Run deep-paging (pages 10-15)"], "ci"),
                MakeJob(id, 4, "scenario-tests", scenario != "pass" ? RunState.Failure : RunState.Success, started, TimeSpan.FromSeconds(240), ["Checkout", "Install browsers", "Run scenarios"], "ci"),
            ];
            if (pagingFail) _logs[id * 10 + 3] = LogFor("paging", id, 3);
            if (scenario != "pass") _logs[id * 10 + 4] = LogFor(scenario, id, 4);
        }
        // smoke workflow: healthy, one run per day
        for (var n = 0; n < 10; n++)
        {
            var id = 2_100_000 + n; var started = _now.Date.AddHours(6).AddDays(-n);
            if (started > _now) started = started.AddDays(-1);
            runs.Add(new WorkflowRunInfo(id, smoke.Id, smoke.Path, "Production smoke test", 90 - n, 1, RunState.Success, "schedule", "main", Sha(id), "Demo: smoke", "github-actions", started, started, started.AddSeconds(68), $"https://example.invalid/{repo.FullName}/actions/runs/{id}"));
            _jobs[id] = [MakeJob(id, 0, "production-image-smoke-test", RunState.Success, started, TimeSpan.FromSeconds(68), ["Pull image", "Run smoke test"], "smoke")];
        }
        _runs[repo] = runs;
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

    private JobInfo MakeJob(long runId, int k, string name, RunState state, DateTimeOffset started, TimeSpan dur, string[] steps, string tag)
    {
        var failIdx = state == RunState.Failure ? steps.Length - 1 : -1;
        var list = steps.Select((s, idx) => new StepInfo(idx + 1, s,
            state.IsActive() ? (idx == 0 ? RunState.InProgress : RunState.Queued)
            : state == RunState.Cancelled ? (idx < steps.Length - 1 ? RunState.Success : RunState.Cancelled)
            : failIdx >= 0 ? (idx < failIdx ? RunState.Success : RunState.Failure) : RunState.Success)).ToList();
        return new JobInfo(runId * 10 + k, name, state, started, state.IsActive() ? null : started + dur, list, $"https://example.invalid/jobs/{runId * 10 + k}");
    }

    private string LogFor(string kind, long runId, int k)
    {
        var ts = _now.AddMinutes(-(runId % 500)).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var sha = Sha(runId);
        var ws = "/home/runner/work/synthetic/synthetic";
        var err = kind switch
        {
            "paging" => "TimeoutError: page.goto: Timeout 30000ms exceeded while loading reviews page 12" + $" (request {Guid.NewGuid():D})",
            "browser" => "Error: browserType.launch: Executable doesn't exist at " + ws + "/.cache/camoufox/firefox",
            "assertion" => $"AssertionError: expected {3 + runId % 4} reviews but received 0",
            "scan" => "error TRIVY0001: HIGH vulnerability CVE-2026-1234 in libssl3 (container image sha256:" + sha + ")",
            "link" => $"Error: 3 broken links found in docs (see {ws}/docs/index.md)",
            "e2e" => "Error: locator.click: Test timeout of 30000ms exceeded.",
            _ => "Error: build step reported failures",
        };
        return $"{ts} ##[group]Run tests\n{ts} noise line {runId}\n{ts} ##[endgroup]\n{ts} ##[error]{err}\n{ts} ##[error]Process completed with exit code 1.\n";
    }
}

