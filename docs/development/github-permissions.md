# GitHub permissions

Create a **fine-grained personal access token** (Settings → Developer settings → Fine-grained tokens), scoped to the repositories you want to view, with **read-only** permissions:

| Permission | Level | Used for | If missing |
|---|---|---|---|
| Metadata | Read | repository discovery | discovery fails (401/403) |
| Actions | Read | workflows, runs, jobs, logs | "lacks permission" on workflows; Actions check fails |
| Contents | Read | `.github/ciworkbench.yml` (or legacy `.ciworkbench.yml`), workflow YAML comments | grouping falls back to defaults; Contents check fails |

Do **not** grant Actions: Write or Workflows: Write. CIWorkbench never modifies anything.

Settings → *Permissions check* runs one real API call per capability (`GET /user`, `GET /user/repos`, `GET /repos/{r}/actions/workflows`, `GET /repos/{r}/contents/.github/ciworkbench.yml`, then the legacy `.ciworkbench.yml`) and reports each separately. A 404 for the config files is fine (the file is optional).

Notes: org repositories may need the organization to allow fine-grained tokens. SSO-protected orgs require authorizing the token. Rate limits: 5,000 requests/hour for a user token; the UI shows remaining budget and waits (up to 30 s) on a limit, otherwise reports when it resets.


## In-app setup guide

When no token is configured, **Settings** (and **Overview**, after choosing *Connect GitHub* / *Live GitHub*) shows a short setup guide with the three read-only permissions above, the token page link and both `dotnet user-secrets` commands (standalone host and the optional AppHost). The token is read at startup, so **restart CIWorkbench** after storing it; the guide then turns into a connected state with *Verify permissions*, *Rediscover repositories* and an organization picker. If the host listens beyond localhost, live mode is disabled on purpose and the guide says so instead of asking for a token.

## Overview scan bounds

The global Overview checks repositories progressively: the 12 most recently pushed repositories (3 at a time), each with one workflow list and one window of recent runs (100 live, 250 demo; the same request the repository page makes, so both share the cache). It never loads jobs or logs by itself; the failing step of a row is one `jobs` request made when you expand that row. *Check N more* scans the next batch. Repositories not scanned yet are shown as "Not checked", never as healthy. "Recurring" on the dashboard means failed two or more times in a row; log-based failure series are analysed on the failure-series pages.
