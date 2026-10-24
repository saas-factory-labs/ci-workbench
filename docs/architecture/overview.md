# Architecture overview

```text
Web (host)  ──►  Components (RCL)  ──►  Core  ◄──  GitHub (provider)
 shell, DI        reusable UI           models, logic    REST + auth seam
```

- **Core**: `ICiProvider` is the single read-only seam (repos paged, workflows, repo-wide runs, runs by workflow, jobs+steps, bounded log tail, file content, rate limit, connection checks). `DemoCiProvider` and `GitHubCiProvider` implement it. Pure logic: `WorkflowGroupResolver`, `RunFiltering`, `SignatureExtractor`, `FailureSeriesBuilder`, `AgentContext`. `TimeProvider` is injected wherever time matters.
- **GitHub**: `GitHubCiProvider` (pagination via `Link`, ETag revalidation, manual log redirect without credentials), `RetryHandler` (primary/secondary rate limit waits up to a cap, 5xx backoff, never retries 401/403-permission/404), `CredentialHandler` (token resolved per request, only attached to the API host), `RateLimitTracker`.
- **Components**: `RepositoryWorkspace` (+ `GroupNode`, `WorkflowNode`, `RunTable`, `FailureSeriesList`), `RunInspector`, `DensityToggle`, `CopyContextButton`. State services: `UiPreferences`, `InspectorState`, `WorkspaceSignals`, registered via `AddCIWorkbenchComponents()`.
- **Web**: Blazor Server (prerender disabled so data loads once), `AppSession` (data mode, progressive discovery), `SwitchableCiProvider`, shell layout, pages.

## Loading strategy

1. Repository discovery streams pages; UI is usable before it ends; cancellable.
2. Opening a repo: workflows + one bounded repo-wide runs request + `.ciworkbench.yml` in parallel.
3. YAML-comment groups need file contents: fetched after first render for workflows not placed by central config, concurrency 4. The UI shows "resolving groups".
4. Jobs load when a workflow is expanded; logs only on click. Failure analysis is bounded (newest 15 failed runs per workflow). In demo mode and for repositories with 3 workflows or fewer it runs automatically for up to 3 workflows with at least two failures; for large live repositories it is lazy: the first view opens the most relevant workflow and analyzes only that one; other workflows are analyzed when expanded, or via "Find recurring failures" (at most 5). When the shared repo-wide run window is full, selecting a workflow with fewer than 10 runs in it fetches one bounded page (30) of that workflow's own history first.
5. Polling: 30 s in live mode with ETags (a 304 is cheap), manual refresh throttled.

## Failure fingerprinting

Key = repo + workflow path + job (matrix suffix stripped) + failed step + normalized signature. Exact equality only (no fuzzy matching). Signature: first non-generic `##[error]` line, else a known tool shape (MSBuild `CS####`, `npm ERR!`, `AssertionError`, ...), else the last non-generic line before the exit-code line; "exit code 1" alone is never a signature. Normalization strips ANSI, timestamps, GUIDs, hashes, runner paths, durations, ports, large numbers and line/column. Unknown signatures merge only within the same step and are flagged "cause unknown"; unavailable logs are a separate state. Streaks ignore cancelled/skipped/active runs, stop at a success or a different failure, and show `≥N` when the window ends or an older failed run was not inspected.

Known limits: a renamed step starts a new series; rerun attempts use the API's latest attempt without flaky tracking.

## Auth seam

`IGitHubCredentialSource.GetAsync(CiScope)` returns a credential per request (`PersonalAccessToken` now; `AppInstallation`/`UserToServer` reserved). Repository-level authorization for a hosted edition would be an additional decorator over `ICiProvider` (not built).

