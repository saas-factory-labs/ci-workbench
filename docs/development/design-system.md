# Design system 2.0: tokens, themes and how they are verified

CIWorkbench ships two first-class themes (Light and Dark) plus **System**, which follows the operating system and is the default. This page explains how colour, type and theme selection are put together so a change stays one edit in one place.

## Token architecture

Everything visual is a CSS custom property with the `--ciw-` prefix. There are three layers and components only ever read the third:

| Layer | Where | Contents |
|---|---|---|
| Palette | `:root` (Dark, the default) and `:root[data-theme="light"]` in `CIWorkbench.Components/wwwroot/ciworkbench.css` | Surfaces, lines, text, brand, status, elevation, code/log colours. The **only** place colour literals may appear. |
| Layout and type | `.ciw-root` (and `[data-ciw-density="compact"]`) | Radii, font stack, type scale, spacing, row heights, motion (`--ciw-t: 140ms`). A host overrides any of them by scoping its own rule. |
| Legacy aliases | `.ciw-root` | `--ciw-fg`, `--ciw-muted`, `--ciw-accent`, `--ciw-failure`, `--ciw-warn`, `--ciw-surface-2` ... still work and resolve to the semantic names. |

Semantic names (a selection): `--ciw-bg`, `--ciw-surface`, `--ciw-surface-raised`, `--ciw-surface-hover`, `--ciw-surface-selected`, `--ciw-text-primary|secondary|muted`, `--ciw-border`, `--ciw-border-strong`, `--ciw-border-control` (3:1 outline for inputs), `--ciw-brand`, `--ciw-brand-hover`, `--ciw-brand-subtle`, `--ciw-link`, `--ciw-focus-ring`, `--ciw-success|danger|warning|info` with `-soft` (badge background) and `-fill` (graphics), `--ciw-shadow-sm|md|overlay`, `--ciw-scrim`, `--ciw-code-*`.

Rules, enforced by `DesignTokenTests`:

* No colour literal (`#hex`, `rgb()`, `hsl()`, `white`, `black`) in `ciworkbench.css` outside the two palette blocks, nor in `app.css` or `ReconnectModal.razor.css`.
* Light and Dark define exactly the same token set.
* Every `var(--ciw-*)` that is used is defined.
* Contrast: body and secondary text 4.5:1 or better (primary 7:1), links 4.5:1, status text on its tinted badge 4.5:1 over every surface it appears on, indicators and control outlines 3:1, log text and error lines 4.5:1+, in both themes.
* No `font-size` below 12 px.

Light is its own palette, not an inversion: white cards on a cool grey page, crisp `#dde5ef` hairlines, soft cool shadows, darker status hues (for example success `#0f6e46`, danger `#b11f2e`) so tinted badges stay legible, and a light code viewer.

Values that differ from the first suggested palette, and why: Light success `#11754b` became `#0f6e46` and danger `#c92a3b` became `#b11f2e` (badge text on a tint of the same hue reached only 4.4:1); Light neutral is `#536477`. Dark keeps the suggested background, surfaces, text, brand, success and danger; the suggested border `#324154` is `--ciw-border-strong`, with a quieter `--ciw-border` (`#273445`) for hairlines and a lighter `--ciw-border-control` (`#5f7089`) for input outlines, which need 3:1. `--ciw-info` is Dark `#7fb0ff` so it is also usable as text.

## Theme selection

* **Source of truth in the browser:** `ciw-theme.js`, a small external script loaded synchronously in `<head>` before the stylesheets (see `App.razor`). It reads `localStorage["ciw:theme"]` (`light`, `dark` or `system`; absent means `system`), resolves it with `matchMedia("(prefers-color-scheme: dark)")` and stamps `<html data-theme="light|dark" data-theme-pref="...">` plus `color-scheme` and the `theme-color` meta before the first paint. That is why neither theme flashes the other.
* **Strict CSP:** the script is an external file, never inline, so `script-src 'self'` needs no nonce or hash. (No CSP is configured today; this keeps one possible.)
* **Live updates:** a `change` listener on the media query re-resolves while System is selected; a `storage` listener syncs other tabs; a `MutationObserver` restores the attributes if Blazor enhanced navigation ever patches `<html>`.
* **Source of truth in the circuit:** `ThemeState` (scoped, in `CIWorkbench.Components`). `MainLayout` reads the stored value after the first render (`ciwTheme.get`), and on every change calls `ciwTheme.set`. Two controls bind to it: `ThemeMenu` (topbar dropdown, desktop) and `ThemeToggle` (segmented, in Settings and in the phone "more" menu). Only the preference string crosses the interop boundary; filters, expanded groups, repository and inspector state live in other services and are untouched by a theme change.
* **Storage:** non-sensitive UI preference only. Never a token. "Reset saved preferences" in Settings returns the theme to System.
* **Blazor Server:** pages render with `prerender: false`, so the HTML shell is static and the circuit never re-renders `<html>`; there is no hydration step that could disagree with the attribute.

## Micro-visualizations (real loaded data only)

* **Workflow Details, "Recent outcomes"** (`WorkflowOutcomes.razor`): a proportional bar and a timeline of the loaded run window with the window named ("newest 100 loaded runs, not lifetime", `+` when more exist), a legend with counts, current streak and last success. Succeeded, failed, cancelled, skipped or neutral, running and queued are separate buckets; none is folded into failure. Every figure has a text equivalent (`aria-label`, legend, facts line). The timeline shows the newest 60; the run table below holds all of them.
* **Quick Preview run indicators:** each of the recent runs is a link to its Run Details with an accessible name ("Run #206, Success, 6 days ago") and a hover/focus tooltip. Shapes differ as well as colours: filled disc passed, crossed disc failed, ring cancelled or skipped, ring with dot running, amber ring queued.
* **Run Details:** the failed job sits in a red-edged callout, the failed step is a chip, the detected error is its own block, and the matching log line is highlighted in the excerpt. Redaction (`AgentContext.Redact`) is unchanged.

## Verification tooling (`tests/visual`)

A small Node project reusing Playwright, with no new framework:

```powershell
cd tests/visual; npm ci
# 1) run the app in demo mode on a port you own (never kill someone else's process)
$env:CIWORKBENCH_MODE="demo"; dotnet run --project ../../src/CIWorkbench.Web --urls http://127.0.0.1:5191
# 2) in another terminal
node --test theme.spec.mjs                                  # Light / Dark / System behaviour (CIW_BROWSER=webkit for WebKit)
node audit.mjs --only 375x667,1366x768                       # axe-core (WCAG 2.2 A/AA) + page overflow, every page state, both themes
node shoot.mjs --out ../../artifacts/visual/after            # screenshot matrix, 9 viewports x 7 targets x 2 themes
```

`shoot.mjs` writes only into the directory you name; it never overwrites an approved baseline. Relative times and clocks are rewritten to fixed tokens just before capture so images are comparable. To compare against an older revision, check it out in a separate worktree, build it to its own output, serve it on another port and run `shoot.mjs --base` against both.

Browser emulation (Chromium, Playwright WebKit) is not a substitute for real Safari; see [responsive-and-mobile-testing.md](responsive-and-mobile-testing.md) for the physical iPhone steps, including both themes.
