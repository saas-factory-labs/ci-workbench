# Hackathon notes

**Pitch:** CIWorkbench turns a flat wall of GitHub Actions runs into an organized workspace and tells you *what keeps failing*.

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

