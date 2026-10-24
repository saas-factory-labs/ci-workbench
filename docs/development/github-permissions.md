# GitHub permissions

Create a **fine-grained personal access token** (Settings → Developer settings → Fine-grained tokens), scoped to the repositories you want to view, with **read-only** permissions:

| Permission | Level | Used for | If missing |
|---|---|---|---|
| Metadata | Read | repository discovery | discovery fails (401/403) |
| Actions | Read | workflows, runs, jobs, logs | "lacks permission" on workflows; Actions check fails |
| Contents | Read | `.ciworkbench.yml`, workflow YAML comments | grouping falls back to defaults; Contents check fails |

Do **not** grant Actions: Write or Workflows: Write. CIWorkbench never modifies anything.

Settings → *Permissions check* runs one real API call per capability (`GET /user`, `GET /user/repos`, `GET /repos/{r}/actions/workflows`, `GET /repos/{r}/contents/.ciworkbench.yml`) and reports each separately. A 404 for `.ciworkbench.yml` is fine (the file is optional).

Notes: org repositories may need the organization to allow fine-grained tokens. SSO-protected orgs require authorizing the token. Rate limits: 5,000 requests/hour for a user token; the UI shows remaining budget and waits (up to 30 s) on a limit, otherwise reports when it resets.

