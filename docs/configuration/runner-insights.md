# Runner Insights

Runner Insights shows which GitHub Actions runner providers your repositories use, how much of each provider's usage has been consumed this billing period, which repositories and workflows consume it, and how trustworthy and complete each number is. It is part of the Community edition (no licence check) and strictly read-only.

Where it appears:

- **Overview → Runner usage**, below the CI health sections: one compact card per provider, with GitHub split into *Organization billing* (verified) and *Observed workflow usage* (estimated).
- **Repository → Runners**: usage grouped by provider and runner label, each workflow's *configured* runner next to what *actually ran*, and links to the latest run on that runner.
- **Settings → Runner Insights**: providers and API status, **provider allowances** (editable), data collection, and advanced details.

## Three separate dimensions

Status badges describe different things and are never folded into one:

| Dimension | Values | Meaning |
|---|---|---|
| Data quality | **Verified** / **Estimated** | Verified = read from GitHub's official billing API. Estimated = calculated from job history. Runtime computed from job timestamps is never "Verified". |
| Completeness | **Partial** / **Complete** | Whether every finished run of the billing period has been collected. |
| Permission | **Missing permission** / **Unavailable** | Why an optional source (billing, variables) could not be read. |

While collection is **Partial**, observed usage is shown as "at least X" / "Measured so far", and **no remaining allowance is derived from it**. The only exception is GitHub's official billing report, which covers the whole account regardless of how many runs were collected.

## Setup and permissions

Nothing extra is needed for estimated usage: **Actions: Read** (already required) gives job history with runner labels. Optional permissions only improve accuracy:

| Capability | Permission | Without it |
|---|---|---|
| Estimated usage per provider | Actions: Read (required for the app anyway) | n/a |
| Configured runners (`runs-on` in workflow files) | Contents: Read | the workflow shows "definition not read" (not an invalid runner) |
| `vars.CI_RUNNER_*` resolution | Variables: Read (repository, and organization for shared variables) | configured runner shown as *unknown (dynamic)*, never guessed |
| Verified GitHub billing, organizations | Organization permission **Administration: Read** (organization owner or billing manager) | usage is estimated; Settings shows the reason |
| Verified GitHub billing, personal accounts | User permission **Plan: Read** (GitHub answers 404 without it) | same |

Settings → *Permissions check* lists the optional checks as **Skipped** (not Failed) when a permission is missing, so a token with Actions read alone still reports a healthy connection.

## What comes from where

| Provider | Label rule | Usage source | Unit |
|---|---|---|---|
| GitHub Actions | `ubuntu-*`, `windows-*`, `macos-*` (standard) | official billing report when readable; job history otherwise | minutes, weighted Linux 1×, Windows 2×, macOS 10× |
| GitHub larger runners | `ubuntu-latest-8-cores`, `*-xlarge`, … | job history | minutes (never from included minutes) |
| Tenki | `tenki-*` (`-Nc-` = cores, default 2) | job history | core-minutes |
| Avrea | `avrea-*` (`-N-vcpu`, default 2) | job history | normalized 2-vCPU minutes |
| BuildPulse | `bp-*` | job history | normalized 2-vCPU minutes (runner time, not its test-result usage API) |
| Celesto | `celesto-*` | job history | minutes; prepaid credit is separate |
| Hetzner ARC | `hetzner-arc*` | job history | minutes; hosting cost is **not tracked** (and is not zero) |
| Blacksmith | `blacksmith-*` | job history | normalized 2-vCPU minutes; its own usage CLI is not integrated |
| Self-hosted (other) / Unrecognized | `self-hosted` / anything else | job history | minutes. Never dropped, so totals stay honest |

Identification is by label **prefix** in a fixed order, never by substring (`avrea-ubuntu-latest` is Avrea, not GitHub). Add providers or custom labels with `RunnerInsights:CustomRules`. Units are never added across providers.

Tenki, Avrea, BuildPulse, Celesto and Blacksmith have no usage API reachable from GitHub credentials, so their numbers are always estimates.

## Provider allowances (Settings)

No provider has a built-in allowance. **Settings → Runner Insights → Provider allowances** has one row per provider, and one row **per GitHub account** (included minutes belong to the account, not to a repository). Each row shows its source: *Server config* (read-only), *Saved* (editable here) or none ("Allowance unknown").

Types: **Monthly recurring allowance**, **One-time promotional credit**, **Prepaid balance**, **Unlimited / no cap**, or none. Amounts can be in the provider's unit or in USD credits with a rate (USD per unit). Providers other than GitHub have a configurable billing-cycle start day (1-28, UTC); GitHub is always the calendar month.

- *Suggestions* are shown where a figure is publicly documented (for example GitHub plan allowances, Blacksmith's free tier, the figure SaaS-Factory's router assumes for Avrea). They only pre-fill the form, are labelled "not verified for your account", and are never confirmed automatically. Tenki, Celesto, BuildPulse and Hetzner ARC have none: Tenki's credit policy has changed over time (monthly top-up, one-time, none), so nothing is assumed; read the balance from the Tenki dashboard.
- **Confirmed** is the user's statement that the numbers match the account. Unconfirmed values are labelled as assumptions.
- *Monthly recurring*: remaining = allowance − usage, shown only from complete data (official GitHub usage, or fully collected jobs) and labelled "estimated".
- *One-time credit / prepaid*: enter the **balance and the date you read it**. Remaining = balance − observed usage since then, only while collection is complete and the balance was read in the current period. Without a balance, only this period's usage is shown.
- Hetzner ARC and other self-managed runners have no allowance and no cost claim.

### Where allowances are stored

Saved allowances live **on the server** in one small JSON file (`runner-insights.json` in `RunnerInsights:DataDirectory`, default `%LOCALAPPDATA%/CIWorkbench` or the platform equivalent; mount a volume at that path in Docker). There is no database and nothing is kept in the browser. Entries are partitioned by a hash of the credential scope (data source, token fingerprint, repository allowlist), so a different token or the demo never sees them, and no token or repository name is written. Writes are atomic (temp file then replace); a corrupt file is ignored. Demo allowances live in memory only.

**Server configuration wins.** Anything under `RunnerInsights:Providers` (appsettings or environment) overrides saved allowances and is read-only in the UI:

```
RunnerInsights__Providers__github.my-org__Allowance=MonthlyIncluded   # key: provider, or provider.account for GitHub
RunnerInsights__Providers__github.my-org__AllowanceAmount=3000
RunnerInsights__Providers__tenki__Allowance=OneTimeCredit
RunnerInsights__DataDirectory=/data
```

## Data collection and completeness

- Each repository's runs of the billing period are listed with `created=>=<period start>` (paged at 100). GitHub caps a filtered listing at **1,000 results**, so a repository at or above that cap is reported as Partial instead of presenting the cap as the whole period.
- Each finished run's jobs are listed once with `filter=all` (so earlier attempts of a rerun are included) and **remembered process-wide**, keyed by run and attempt, partitioned by data source and credential fingerprint. Jobs are deduplicated by their immutable GitHub job id, so reruns and matrix legs count exactly once and a refresh never counts a job twice.
- Collection is bounded and resumable. An automatic pass makes at most `MaxJobRequestsPerRefresh` (default 100) job requests, newest runs first; **Collect remaining** makes up to `MaxJobRequestsPerCollect` (default 500) and can be repeated. Both pause when GitHub's rate limit drops to `RateLimitReserve` (default 300) remaining requests. A refresh re-lists runs and billing, then only fetches runs it has not seen.
- Coverage shows runs collected out of runs that exist, repositories in scope, and what is still missing. It is tracked **per account** (GitHub allowances are per account) and overall (shared provider accounts). Repositories the Overview has not scanned yet count as not collected ("Check N more" on the Overview widens the scope).
- Only jobs that **started and completed** within the period count; queue time (`created_at → started_at`) is not billed; queued, in-progress and skipped jobs never count.
- Rounding: GitHub rounds each job up to the next minute (documented). The other vendors use the same per-job round-up, as SaaS-Factory's router does; that is an **assumption** for those vendors. Self-hosted time is exact. Standard GitHub runners in public repositories are free: measured, but they draw nothing from an allowance.

## GitHub billing API

Runner Insights uses `GET /organizations/{org}/settings/billing/usage` (or `/users/{username}/settings/billing/usage`) with `year` and `month`, keeping the Actions product. The deprecated `/settings/billing/actions` endpoint (now answering 410) is not used. The newer `usage/summary` endpoint (public preview) was evaluated and not adopted. The report includes non-minute lines (for example *Actions storage*, in GigabyteHours); only `Minutes` lines are shown as billed minutes, and only standard Linux/Windows/macOS SKUs are weighted against included minutes.

- **Verified**: billed minutes, gross/discount/net amounts, and "free minutes used up" (a net charge on a standard SKU).
- **Not proof of anything**: a discount or a zero-dollar bill. It does not show that free minutes remain.
- **Never verified**: how many included minutes your plan has. GitHub's API does not report it, so it is a configured allowance, and remaining is labelled estimated.

The official report is **account-wide** (all repositories, including any not collected and any outside the Overview scope), while observed usage covers only collected jobs. They are shown as two separate measurements and are not expected to match until collection is complete (and even then only for the same runner types and repositories).

## Configured vs actual runners

`runs-on` is read from workflow files on the default branch: labels, label arrays, `{group, labels}`, `matrix.*` with literal values, local `uses: ./.github/workflows/*.yml` (one level), and `fromJSON(vars.X || '[...]')`. Each runner is classified:

- **configured**: what the workflow resolves to now;
- **potential fallback**: the literal after `||` when the variable is set or unreadable. Never reported as active;
- **unknown (dynamic)**: computed matrices, `inputs.*`, remote reusable workflows and other expressions.

Because routers such as SaaS-Factory's BillingSwitcher rewrite variables periodically, a variable shows the configuration *now*. History always comes from executed jobs. A workflow that exists only in history is marked *historical* and still shows what ran; a workflow file that could not be read (or exists only on another branch) shows "definition not read", which says nothing about its runners; GitHub-managed dynamic workflows (Copilot, dependency graph, Dependabot) have no file at all.

## Limitations

- Estimates are not provider billing data. Vendor rounding, RAM-specific pricing (Avrea), plan changes and the 2-vCPU normalization of BuildPulse/Blacksmith are modelled approximately.
- A run still in progress contributes after it completes; a rerun in progress hides its earlier attempts until it finishes. Runs created before the period whose jobs ran inside it are not listed (the listing is by run creation date).
- Celesto prepaid balances, Hetzner hosting cost and Blacksmith's CLI usage are out of scope. Advanced cost optimization, history, forecasting and savings recommendations are reserved for a future Pro edition.
