# Embedding CIWorkbench in a host application

CIWorkbench Community can be hosted inside another Blazor Server application (SaaS Factory's Deployment Manager is the first one).
**No host-specific code lives in this repository**: the host owns its shell, navigation, authentication and authorization; CIWorkbench owns everything about CI.

## Packages

| Package | Contents |
|---|---|
| `SaaS-Factory.CIWorkbench.Core` | Models, failure/series analysis, health, overview and runner-usage logic. |
| `SaaS-Factory.CIWorkbench.GitHub` | `ICiProvider` over the GitHub REST API (read-only), `IGitHubCredentialSource`, rate-limit handling, telemetry. |
| `SaaS-Factory.CIWorkbench.Components` | Razor components plus `ciworkbench.css`, `ciworkbench.js`, `ciw-theme.js` and the Inter font as static web assets. |

The three share one version (`CIWorkbenchVersion` in `Directory.Build.props`) and are released together. Referencing `Components` is enough. Install:

```
dotnet add package SaaS-Factory.CIWorkbench.Components --version 0.1.0-preview.1
```

Packages are published to GitHub Packages (`https://nuget.pkg.github.com/saas-factory-labs/index.json`) by the manually triggered, approval-gated
`Release packages` workflow. Add that source, with a token that has `read:packages`, to the consumer's `NuGet.config`, and map the `SaaS-Factory.CIWorkbench.*` pattern to it if you use package source mapping.
`scripts/verify-packages.sh` (also run in CI) packs, inspects the packages and restores them into a minimal external host from a clean cache.

## Wiring a host

1. **Services**

   ```csharp
   builder.Services.AddCIWorkbenchGitHub(builder.Configuration);     // options: "GitHub" section; token never leaves the server
   builder.Services.AddCIWorkbenchWorkspace(builder.Configuration);  // session, Overview data, Runner Usage, scoped ICiProvider
   ```

   Set `CIWorkbench:Mode` to `live` so the session starts on GitHub data (without a credential it falls back to demo data and says so).
2. **Credentials.** The default `PatCredentialSource` reads `GitHub:Token` (use user-secrets, an environment variable or your secret store). To supply a credential another way,
   register your own `IGitHubCredentialSource` **before** `AddCIWorkbenchGitHub`; override `IsConfigured` so the UI knows whether to offer live data. The token is attached to GitHub API requests server-side only and never reaches HTML, JavaScript or browser storage.
3. **Assets.** In the document head: `_content/CIWorkbench.Components/ciworkbench.css` (and `ciw-theme.js` if you want its theme handling); at the end of the body: `ciworkbench.js`.
   Call `builder.WebHost.UseStaticWebAssets()` if the host runs outside the Development environment.
4. **Routes.** The components contain no `@page`, layout, auth or hard-coded URLs. Add your own pages and render:
   `OverviewDashboard`, `RepositoryView` (`Owner`, `Name`; add `/failures` for the series view), `WorkflowView` (`Owner`, `Name`, `WorkflowId`), `RunView` (`Owner`, `Name`, `RunId`).
   Subclass `CiwRoutes` to place them under your own path and register it as a singleton before `AddCIWorkbenchWorkspace`; override `HasSettings` to `false` if you host no settings pages.
   Links are relative to the document base, so return paths without a leading slash.
5. **Authentication and authorization** are yours: put the pages behind your policies (`[Authorize]`). CIWorkbench adds none.
6. **Theme.** Everything is scoped under `.ciw-root` and driven by `--ciw-*` custom properties. Override them in the host's stylesheet to match its theme.
7. **Telemetry.** Instrumentation is opt-in. To keep repository names and signed log URLs out of traces, add `AddMeter/AddSource(CiWorkbenchTelemetry.Name)` and filter generic HttpClient instrumentation with `!CiWorkbenchTelemetry.IsSuppressed(request)`.
8. **HTTP resilience.** The GitHub clients carry their own retry handler (rate-limit aware). If the host adds a global standard resilience handler to every `HttpClient`, remove it for these clients (`RemoveAllResilienceHandlers`) to avoid stacked retries.
9. **State on disk.** Runner Usage allowances saved from the standalone Settings page live in a JSON file (`RunnerInsights:DataDirectory`). A host without a settings page does not write it; set the directory to a persistent path if you use server configuration (`RunnerInsights:Providers`).

## What stays out of this repository

- Host navigation entries, layouts, authentication and authorization policies.
- Per-user repository authorization and GitHub App credentials (future work; they plug in by wrapping `ICiProvider` and implementing `IGitHubCredentialSource`).
- Audit logging around log access (host-side).
