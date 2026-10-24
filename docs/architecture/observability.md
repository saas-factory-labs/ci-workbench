# Observability and .NET Aspire

Aspire is **optional**. `CIWorkbench.Web` runs standalone; `CIWorkbench.AppHost` only adds a dashboard and orchestration. Nothing here needs Docker, PostgreSQL, Redis or any other infrastructure.

## Projects

- `CIWorkbench.ServiceDefaults`: the standard Aspire service-defaults project (OpenTelemetry, health checks, service discovery), referenced only by the web host. The OTLP exporter is enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, which the AppHost does automatically; a plain `dotnet run` exports nothing.
- `CIWorkbench.AppHost`: registers the web app (`/ready` health check) and passes through two optional settings: the `github-token` secret parameter (as `GitHub__Token`, and sets live mode) and `GitHub:ApiBaseUrl` (GitHub Enterprise Server or a test double).
- `CIWorkbench.GitHub` has **no OpenTelemetry dependency**. It uses only BCL `Meter`, `ActivitySource`, `ILogger` and health-check abstractions, so another host can embed it and opt in with `AddMeter("CIWorkbench.GitHub")` / `AddSource("CIWorkbench.GitHub")`.

## Health endpoints (Development only, as in the Aspire template)

`/alive` (process responsive), `/ready` (adds the GitHub readiness check, which never calls GitHub and is never Unhealthy: demo mode needs no token; it is Degraded while rate-limited) and `/health` (everything).

## Metrics (meter `CIWorkbench.GitHub`)

| Instrument | Labels |
|---|---|
| `ciworkbench.github.request.duration` (s) | `operation`, `status_class` |
| `ciworkbench.github.request.errors` | `operation`, `error_type` (`unauthorized`, `forbidden`, `rate_limited`, `not_found`, `client_error`, `server_error`, `timeout`, `network`) |
| `ciworkbench.github.request.retries` | `reason` (`rate_limit`, `server_error`, `network`) |
| `ciworkbench.github.cache.requests` | `operation`, `result` (`hit` = 304, `miss`) |
| `ciworkbench.github.repository_discovery.duration` (s) | `outcome` (`completed`, `failed`, `cancelled`, `abandoned`) |
| `ciworkbench.github.workflow_load.duration` (s) | `outcome` |
| `ciworkbench.github.ratelimit.remaining` (gauge) | none |

Each GitHub request also produces one `github.request` span (`github.operation`, status code, error class). Optional files that do not exist (for example `.ciworkbench.yml`) are not counted as errors.

## What is never recorded

Tokens, URLs, repository/owner names, response bodies, signed log-download URLs and CI log text never reach metrics, spans or logs. Mechanisms: bounded label vocabularies; GitHub requests are flagged so the generic HttpClient instrumentation (which records full URLs) skips them; `/repo/{owner}/{name}` server-span paths are replaced by the route template; log scopes (which include the request path) are disabled for export; `System.Net.Http.HttpClient` logging (which prints URLs) is raised to Warning. `GitHubHealthCheck` reports only `token_configured` as a boolean.

## HTTP resilience

The GitHub clients use their own retry handler (honors `Retry-After` / `x-ratelimit-reset`, never retries 401, permission-403 or 404). The generic Aspire resilience handler is switched off for this app (`ServiceDefaultsOptions.StandardHttpResilience = false`) so retries do not stack; a test asserts exactly 1 + 3 attempts for a persistent 503. Other hosts keep the template default (on).

## Known limitations

- Blazor Server hub chatter (`ComponentHub/*`) is dropped from traces to keep GitHub spans visible.
- Verified against a local stub of the GitHub API. Live GitHub itself has not been exercised with a real token.