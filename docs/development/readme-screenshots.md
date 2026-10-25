# README screenshots

The six images in `docs/screenshots/dark/` are real captures of the running app in demo mode (Dark theme, Comfortable density, 1600 px wide viewport, 1.5x device scale, WebP). They use only the synthetic demo provider: no token, no GitHub access.

## Regenerate

```powershell
cd tests/visual
npm install
npx playwright install chromium   # first time only
node readme-shots.mjs
```

The script builds `CIWorkbench.Web` into a temporary folder (so a running Aspire/AppHost never has its binaries locked), starts it on port **5197** in demo mode, captures, and stops only that process. Useful options: `--port 5198`, `--only run-details,job-history`, `--out <dir>`, `--scale 2`, or `--base <url>` to reuse a demo-mode server you started yourself.

## Determinism

The server is started with `CIWORKBENCH_DEMO_NOW=2026-10-23T14:00:00Z`. When that variable is set, the web host pins the wall clock (`GetUtcNow` only; timers are unaffected), so every synthetic timestamp such as "4 hours ago" is identical between runs. It is meant for documentation captures and has no effect when unset. Fixtures are the existing ones in `CIWorkbench.Core/Demo`:

| Image | Source |
|---|---|
| repository-workspace, workflow-quick-preview | `example-org/platform-monorepo` (Build & Test and Security groups) |
| workflow-details, run-details, failure-series, job-history | `example-org/review-scraper-actor`, workflow `CI` (28 runs, two recurring series, one one-off) |

Each shot crops to the lowest rendered content, so no empty page area is included.
