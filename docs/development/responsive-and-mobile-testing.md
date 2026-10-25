# Navigation, responsive layout and phone testing

CIWorkbench is one responsive Blazor web application. There is no native app, no second front end and no mobile backend.

## Three levels, each with a direct URL

| Level | Route | Purpose |
|---|---|---|
| 1. Repository workspace | `/repo/{owner}/{repo}` | Which workflows are healthy, which need attention. Groups, three KPIs, filters, inline quick preview. |
| 1b. Failure series (all workflows) | `/repo/{owner}/{repo}/failures` | Recurring failures across the repository. |
| 2. Workflow details | `/repo/{owner}/{repo}/workflows/{workflowId}?tab=runs\|failures\|jobs&status=&branch=&event=` | One workflow: run history, recurring failure series, per-job history. |
| 3. Run details | `/repo/{owner}/{repo}/runs/{runId}?job=` | One run: metadata, failing job and step, steps, logs on demand, related series. |

`/repos/...` is accepted as an alias for the workflow and run routes. The workflow id is GitHub's stable workflow id; grouping still keys on the workflow path.

Click behaviour on the workspace: the **left chevron** opens or closes the inline quick preview, the **workflow title** is a link to Workflow details, a **run number** inside a preview opens Run details, the **group chevron** opens or closes the group. The panel icon (or "Quick inspect") opens the optional Quick Inspector.

## Data loading

* The overview loads workflows, a bounded window of recent runs and the grouping config. It never loads jobs or logs.
* A quick preview loads the jobs of one run (plus a bounded failure analysis when the workflow is failing).
* Workflow details loads that workflow's own history (100 runs, "Load older runs" up to 300).
* Run details loads the run (`GET /actions/runs/{id}`), its jobs and, on demand, a bounded log tail.
* All of this goes through `CachedCi`, a per-circuit TTL/LRU cache (max 400 entries, 24 log tails). Concurrent identical requests share one call; a caller that navigates away stops waiting without cancelling the shared fetch. Refresh drops run lists only. Nothing is persisted, and the cache never sees credentials.

## Layout breakpoints

| CSS width | Behaviour |
|---|---|
| >= 1680 | Quick Inspector docks as a right column. |
| 761 to 1679 | Quick Inspector is a slide-over drawer (the content width does not change). Sidebar collapses to an icon rail below 1181. |
| <= 760 | Hamburger opens a navigation drawer; compact top bar (repository picker, search, more menu); workflow rows and run tables become stacked cards; KPIs become one wide card plus two; Quick Inspector is a full-screen sheet with a Close button. Only one overlay (drawer, actions menu, inspector) is open at a time; Esc closes the topmost. |

The viewport meta uses `viewport-fit=cover`; safe-area insets, `dvh` heights, 16 px form controls (no iOS zoom on focus) and 44 px touch targets (`pointer: coarse`) are applied. Scroll position is saved per URL in `sessionStorage` and restored after back/forward navigation or reload.

## Verifying in a browser

The checks used for this redesign were Playwright scripts driving Chromium and WebKit against a demo-mode instance at 320 x 568 up to 5120 x 1440. They assert no page-level horizontal overflow, no clipped elements and no touch targets under 36 px, and walk the repository, workflow, run, inspector and 320 px journeys. Browser emulation is not a substitute for real Safari.

## Testing on a real iPhone (demo data only)

Live mode holds a GitHub token on a server that has no login, so it must never be reachable from the network. The host enforces this: **if it listens on anything other than loopback, the token is discarded at startup and only synthetic demo data is served.**

1. Put the phone and the computer on the same trusted Wi-Fi.
2. Run the LAN profile, which binds all interfaces in demo mode:

   ```powershell
   dotnet run --project src/CIWorkbench.Web --launch-profile lan-demo
   ```

   (equivalent: `$env:CIWORKBENCH_MODE="demo"; dotnet run --project src/CIWorkbench.Web --urls http://0.0.0.0:5290`).
3. Allow the port through Windows Firewall for the **Private** profile only, for example `New-NetFirewallRule -DisplayName "CIWorkbench demo" -Direction Inbound -Protocol TCP -LocalPort 5290 -Profile Private -Action Allow`. Remove the rule afterwards.
4. Find the computer's address (`ipconfig`, the IPv4 of the Wi-Fi adapter) and open `http://<that address>:5290` in Safari on the phone.
5. Stop the server when you are done.

Implications: anyone on that network can open the demo UI (synthetic data only, no token, no GitHub access). Do not use this profile with a token configured on a shared or public network. Use plain HTTP only for demo data; the async clipboard API needs a secure context, so on `http://` addresses the copy buttons use the older `execCommand` fallback (which may be blocked by some browsers).

Things worth checking on the phone: the toolbar collapsing while scrolling, rotation, the on-screen keyboard in the search box, the navigation drawer and inspector sheet near the home indicator, and back-swipe from Run details to Workflow details to the workspace.

### Theme checks on the phone (Light, Dark, System)

Run these once with the iPhone set to Light appearance and once with Dark (Settings, Display and Brightness), because System follows it.

1. Open the site fresh (close the tab first). It must paint in the iPhone's current appearance with no flash of the other theme. Fresh means no saved choice, so the Settings theme control shows **System**.
2. Tap the three-dot menu in the top bar. The **Light / Dark / System** control is there (the top bar itself has no theme buttons). Each segment is at least 44 pt tall.
3. Choose Dark, then Light: the whole page changes at once and the repository, filters, expanded workflows and an open inspector stay as they were.
4. Choose System, then switch the phone between Light and Dark from Control Centre with Safari open: the page follows within a moment, without a reload.
5. Reload and navigate to another page: the explicit choice persists. Quit and reopen Safari: it still persists (it is stored in this browser only).
6. In both themes check: card and row boundaries are visible on the small screen, the failed-job callout and log excerpt are readable (the long log line scrolls inside its own box, not the page), status badges are legible, the drawer and the inspector sheet clear the home indicator, and rotating to landscape keeps everything reachable.
7. Long-press a recent-run dot in a quick preview: it should open that run (the dots are links) and nothing is clipped at 320 pt width.

This is the only real-Safari verification. The automated WebKit runs use Playwright's WebKit build, which shares the engine but not iOS Safari's chrome, safe areas or dynamic toolbar.

