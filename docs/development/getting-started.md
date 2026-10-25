# Getting started

```powershell
git clone https://github.com/saas-factory-labs/ci-workbench
cd ci-workbench
dotnet build CIWorkbench.slnx
dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290
```

Demo mode needs no credentials. `NuGet.config` uses nuget.org only; no private feed is needed.

## Live mode

See [github-permissions.md](github-permissions.md). Use user-secrets (`dotnet user-secrets set "GitHub:Token" ...`) or the `GitHub__Token` environment variable. Never put a token in `appsettings*.json`.

## Tests

```powershell
dotnet test --project tests/CIWorkbench.Core.Tests
dotnet test --project tests/CIWorkbench.GitHub.Tests
dotnet test --project tests/CIWorkbench.Web.Tests
```

Core tests are pure; GitHub tests use a fake `HttpMessageHandler` (no network); Web tests use bUnit and an in-memory test server.

## Tips

- The ports 8082/8083 etc. are not used; pick any with `--urls`.
- Configuration keys: `GitHub:Token`, `GitHub:ApiBaseUrl`, `GitHub:AllowedRepositories` (list of `owner/name` or `owner/*`), `GitHub:MaxRateLimitWaitSeconds`, `CIWORKBENCH_MODE=live`.


Tests use [TUnit](https://tunit.dev) on the Microsoft Testing Platform (`global.json` selects the runner, so `dotnet test` needs the `--project` form on .NET 10). Do not pass `--nologo` to `dotnet test`: it is forwarded to the test application. Tests run in parallel by default; the GitHub test assembly opts out with `[assembly: NotInParallel]` because its telemetry listeners are process-wide.
