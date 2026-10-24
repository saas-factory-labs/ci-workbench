# Embedding CIWorkbench in a host application (planned)

CIWorkbench is designed so an external admin portal can host it. **No host-specific code lives in this repository.**

## Boundary

- Public: `Core`, `GitHub`, `Components`, `Web` (this repo).
- Host-side adapter (maintained by the host, outside this repo): a thin module that
  1. registers `AddCIWorkbenchComponents()` and an `ICiProvider` (for example `GitHubCiProvider` with the host's credential source implementing `IGitHubCredentialSource`),
  2. adds a navigation entry and routes that render `<RepositoryWorkspace>` / `<RunInspector>` inside the host layout,
  3. maps `OnRunSelected` / `OnViewFailures` callbacks to host navigation,
  4. performs authentication/authorization (`AuthorizeView`, policies) around those pages,
  5. overrides `--ciw-*` CSS custom properties to match the host theme, and includes `ciworkbench.css` / `ciworkbench.js`.

## Why it works

- Components have no `@page`, layout, `NavigationManager` or auth code; navigation is by callbacks.
- Theming is by `--ciw-*` tokens scoped on `.ciw-root`.
- Services are injected (`ICiProvider`, `TimeProvider`, state services).
- The host supplies credentials and, if needed, per-user repository authorization by wrapping `ICiProvider`.

## Remaining work for an embedded host

- A packaged NuGet distribution of the four assemblies (not published yet).
- A repository authorization decorator and GitHub App credential source.
- Host-side audit logging around log access.

