# Hackathon notes

**Pitch:** CIWorkbench turns a flat wall of GitHub Actions runs into an organized workspace and tells you *what keeps failing*.

## Licensing for hackathon participants

CIWorkbench Community is **source-available, not OSI open source**, under **Apache-2.0 plus Commons Clause v1.0**. You may explore it, run it locally, use it internally, modify it, and propose pull requests. Selling CIWorkbench itself or a competing offering whose value derives entirely or substantially from its functionality is restricted; see [LICENSE](LICENSE) and [LICENSING.md](LICENSING.md) for the full terms.

**Code contributors:** Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [CLA.md](CLA.md). Contributors keep their copyright but grant separately documented rights needed for the project to use their code in both Community and commercial products. Maintainers must confirm acceptance before merging third-party code. Non-code UX feedback and bug reports do not require the CLA.

**Scope:** Public Community issues/code only. Proprietary performance optimization, runner-cost simulation and premium MCP capabilities belong in a separate private Pro repository; do not implement or copy Pro internals here.

## 2-minute demo script (no credentials)

1. `dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290`
2. Overview: note the **Demo data** banner and progressive discovery.
3. `/` opens `example-org/platform-monorepo` directly: 39 workflows in 8 groups (config + YAML comments), the banner for 3 recurring series, and the Security group already open on the failing *Container image scan* workflow. Filter **Failed**, search, **Expand all**, toggle **Compact** (a denser, one-line-per-series layout).
4. Open `example-org/review-scraper-actor`: banner shows recurring failures; expand the *deep-paging* series (15 consecutive failures; analysis is bounded to the newest 15 failed runs per workflow and says "Inspected 15 of 15") and the *scenario-tests* browser series. The single `AssertionError` in the same job is deliberately **not** merged and appears under "One-off failures" (a series needs at least 2 matching failures).
5. Click a run: the **Run Inspector** opens, failed jobs first; *Load log excerpt* fetches on demand; *Copy context for coding agent*.
6. Settings: permission checks and data source.

## Implemented vs. not

See the status table in the README. Honest gaps: live GitHub path is tested only against a fake HTTP server; no Playwright suite yet; single-user only.

## Built with

.NET 10, Blazor Server, YamlDotNet, TUnit, bUnit. Designed and implemented with Claude Code (Sonnet 5.5 for implementation, one Opus 5.5 design consultation).

