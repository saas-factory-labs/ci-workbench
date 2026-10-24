# CIWorkbench

**Your CI/CD workspace.** An open-source workbench for organizing GitHub Actions workflows, monitoring runs and troubleshooting recurring failures across repositories. Built with .NET 10 and Blazor. Useful without AI.

> Status: hackathon MVP. See [what is implemented vs. planned](#status).

## Why

GitHub Actions lists workflows flat and per run. With 40 workflows that is hard to navigate, and with a nightly job that keeps failing you cannot see *what* keeps failing. CIWorkbench adds:

- **Workflow groups**: collapsible, hierarchical groups from a central `.ciworkbench.yml` or a `# ci-workbench-group: Security` comment in the workflow file.
- **Recurring failure series**: deterministic fingerprinting (workflow, job, failed step, normalized error signature) that groups repeated failures, shows streaks, and separates unrelated errors in the same job.
- **Run Inspector**: collapsible side panel with jobs, steps, on-demand logs, a link to the original run and a safe **Copy context for coding agent** action (redacted, bounded, never sent anywhere).
- **Honest onboarding**: verifies each permission with the real API call, shows progressive repository discovery, rate-limit state and exact failure reasons.
- **Comfortable / Compact density**, switchable without reload.

## Quick start (demo mode, no credentials)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290
```

Open <http://localhost:5290>. Demo mode uses fully synthetic data (a 39-workflow monorepo and a scraper actor with two recurring failure series) and is labelled as such.

## Live GitHub (read-only)

Create a **fine-grained personal access token** with *read-only* `Metadata`, `Actions` and `Contents` permissions (details: [docs/development/github-permissions.md](docs/development/github-permissions.md)), then supply it **server-side**:

```powershell
dotnet user-secrets set "GitHub:Token" "<token>" --project src/CIWorkbench.Web
$env:CIWORKBENCH_MODE = "live"   # optional; otherwise switch in Settings
dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290
```

The token is never sent to the browser and never stored in `localStorage`. The standalone host is **single-user and meant for localhost** ([SECURITY.md](SECURITY.md)).

## Repository layout

```text
src/
  CIWorkbench.Core/        models, provider seam, grouping, filtering, failure series, demo data (no UI, no HTTP)
  CIWorkbench.GitHub/      read-only GitHub REST provider, credential seam, retry/rate limit/ETag
  CIWorkbench.Components/  reusable Razor class library (workspace, run table, inspector, failure series)
  CIWorkbench.Web/         standalone Blazor Server host (shell, session, onboarding)
tests/                     Core, GitHub (fake HTTP), Web (bUnit + HTTP smoke)
docs/                      architecture and development docs
```

Build and test:

```powershell
dotnet build CIWorkbench.slnx
dotnet test tests/CIWorkbench.Core.Tests; dotnet test tests/CIWorkbench.GitHub.Tests; dotnet test tests/CIWorkbench.Web.Tests
```

## Configuring groups

`.ciworkbench.yml` at the repository root (default branch):

```yaml
groups:
  - name: Build & Test
    workflows: [".github/workflows/ci-*.yml", ".github/workflows/lint.yml"]
  - name: Security/Scanning        # "/" nests groups
    description: CodeQL and image scans
    workflows: [".github/workflows/codeql.yml"]
```

Precedence: **central config → `# ci-workbench-group:` comment → "Ungrouped"**. Workflows are matched by path (never display name). Malformed or missing config never hides workflows; a warning is shown instead.

## Status

| Area | State |
|---|---|
| Demo mode, both fixtures, full UI | Implemented |
| GitHub PAT (server-side), read-only REST provider | Implemented, unit-tested against a fake HTTP server; **not yet exercised against live GitHub in CI** |
| Progressive discovery, cancellation, permission checks, rate-limit awareness | Implemented |
| Workflow groups, filters, density, Run Inspector, failure series, agent-context copy | Implemented |
| GitHub App auth, per-user repository authorization, hosted multi-user mode | Planned (interfaces only, see docs) |
| Azure DevOps / other providers | Planned |
| Webhooks / push updates | Not planned for MVP (30 s polling) |
| Playwright browser tests in CI | Planned (manual browser smoke only so far) |

## Contributing / License

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md) and [HACKATHON.md](HACKATHON.md). Licensed under [Apache-2.0](LICENSE).

