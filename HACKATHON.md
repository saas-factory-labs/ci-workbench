# Hackathon notes

**Pitch:** CIWorkbench turns a flat wall of GitHub Actions runs into an organized workspace and tells you *what keeps failing*.

## 2-minute demo script (no credentials)

1. `dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290`
2. Overview: note the **Demo data** banner and progressive discovery.
3. Open `example-org/platform-monorepo`: 39 workflows in groups (config + YAML comments). Filter **Failed**, search "container", expand all, toggle **Compact**.
4. Open `example-org/review-scraper-actor`: banner shows recurring failures; expand the *deep-paging* series (15 consecutive) and the *scenario-tests* browser series. The one-off `AssertionError` in the same job is deliberately **not** merged.
5. Click a run: the **Run Inspector** opens, failed jobs first; *Load log excerpt* fetches on demand; *Copy context for coding agent*.
6. Settings: permission checks and data source.

## Implemented vs. not

See the status table in the README. Honest gaps: live GitHub path is tested only against a fake HTTP server; no Playwright suite yet; single-user only.

## Built with

.NET 10, Blazor Server, YamlDotNet, xUnit, bUnit. Designed and implemented with Claude Code (Sonnet 5.5 for implementation, one Opus 5.5 design consultation).

