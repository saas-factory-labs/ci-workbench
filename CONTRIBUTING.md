# Contributing

Thanks for helping! You need the .NET 10 SDK and nothing else. No private feeds or credentials are required to build; `NuGet.config` clears all sources except nuget.org.

## Licensing and contributions

CIWorkbench Community is **source-available under the Apache License 2.0 terms with the Commons Clause v1.0 condition**, not OSI-approved open source. Review [LICENSE](LICENSE) and [LICENSING.md](LICENSING.md) before contributing. Internal development and use are permitted; selling the software or a product/service whose value derives entirely or substantially from it is restricted by the full clause. Past Apache-only releases retain their existing grants.

### Contributor rights and CLA

Contributors keep copyright in their original work. Code and documentation contributions intended for inclusion require acceptance of the [CIWorkbench Contributor License Agreement v1.0](CLA.md), which grants the maintainer non-exclusive rights to use and relicense contributed code in both Community and commercial Pro editions.

**How to contribute:**

1. Read [CLA.md](CLA.md) and open a focused pull request.
2. **Post the individual acceptance statement from CLA §7 as a PR comment** (checking the PR template box is not sufficient).
3. If an employer or organization owns the work, obtain acceptance from an authorized representative under CLA §7B before merge.
4. Ensure all contributors and co-authors are covered; identify any third-party licenses.
5. A maintainer reviews consent and authority and records a verification comment with links before merge.

No CLA is needed for issue reports, general suggestions, or usability feedback. CLA acceptance is manually recorded on GitHub; no CLA bot is currently installed. Do not publish private employer authorization documents in a public PR.


## Workflow

1. Fork, create a branch, make a focused change.
2. `dotnet build CIWorkbench.slnx` must have zero warnings (warnings are errors).
3. Run the three test projects (see README). New behavior needs a test; Core logic should be tested as pure functions.
4. Open a PR describing the *why*.

## Running locally

- Standalone: `dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290`
- With the Aspire Dashboard (optional): `dotnet run --project src/CIWorkbench.AppHost`. Aspire must stay optional: the app has to keep starting and working in demo mode without it, and without Docker or any database.
- Optional GitHub token for the AppHost: `dotnet user-secrets set "Parameters:github-token" "<read-only PAT>" --project src/CIWorkbench.AppHost`. Never put tokens in `appsettings*.json` or launch profiles.

## Secret scanning (three layers)

1. **Local pre-commit hook** (`.pre-commit-config.yaml`): GitGuardian `ggshield` scans staged changes, and a validator requires every GitHub Action to be pinned to a 40-character commit SHA. Set it up once per clone:
   ```powershell
   pip install pre-commit ggshield
   ggshield auth login          # free GitGuardian account; stores your key outside the repo
   pre-commit install
   ```
   Run all hooks manually with `pre-commit run --all-files`. A local hook can be skipped with `--no-verify`, so it is a convenience, not the enforcement.
2. **CI**: `.github/workflows/gitleaks.yml` runs [Gitleaks](https://github.com/gitleaks/gitleaks) (open source, no account needed, also for forks; the binary is pinned by version and sha256) over the full history, and `.github/workflows/git-guardian.yml`: runs GitGuardian on pushes to `main`, version tags, pull requests from this repository, and manual dispatch. It needs the repository secret `GITGUARDIAN_API_KEY` and fails loudly when it is missing, so a scan that cannot run never passes silently.
3. **GitHub secret scanning with push protection** is enabled on the repository and rejects pushes that contain recognised provider tokens.

Test fixtures must use obviously fake values (for example `ghp_..._NOT_REAL_...`). If a scanner flags a deliberate dummy, add it to `.gitguardian.yaml` with a reason rather than weakening the hook. Pin new Actions with `uses: owner/repo@<40-hex>  # vX.Y.Z`.

## Ground rules

- **Keep Core host-independent.** `CIWorkbench.Core` has no HTTP or UI dependencies. `CIWorkbench.Components` has no `@page` routes, no layout, no auth checks and no navigation: hosts pass callbacks and register services.
- **Read-only.** Never request or use Actions write / workflow modification permissions.
- **No secrets, no real data.** Fixtures must be synthetic. Never commit tokens, real logs or private repository names.
- **Telemetry is privacy-sensitive.** Metric/span attributes and log fields may only be fixed operation names, status/error classes, counts and durations. Never record tokens, URLs, repository or owner names, response bodies or CI log text; avoid high-cardinality labels. `TelemetryTests` enforces this: extend it when you add telemetry.
- **Credentials stay server-side.** Never put a token in browser storage, URLs or rendered markup.
- **Packages:** central versions in `Directory.Packages.props`; prefer permissively licensed packages (Apache-2.0 compatible).
- **Dependencies on private packages are not accepted.**
- Style: `.editorconfig`; match the surrounding code.

## Where things live

See [docs/architecture/overview.md](docs/architecture/overview.md) and [docs/development/getting-started.md](docs/development/getting-started.md).

