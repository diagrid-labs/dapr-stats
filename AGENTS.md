# AGENTS.md

Notes for AI agents working in this repository. [README.md](README.md) covers what is collected and how to run it; this file covers how the code is put together and what tends to trip people up.

## What this is

A .NET 10 web service that uses Dapr Workflow to collect Dapr SDK and community metrics once a week and store them in a Neon Postgres database. It is a batch job wearing a web service's clothes: the app exposes no endpoints of its own, and everything is driven by starting `CollectorWorkflow` through Dapr's workflow HTTP API.

## Commands

| Task | Command |
|---|---|
| Build | `dotnet build dapr-stats.sln` |
| Test | `dotnet test dapr-stats.sln` |
| Run locally | `dapr run -f .`, then send a request from [local-tests.http](local-tests.http) |

The test suite is 117 xUnit tests that run in about 100 ms. They are pure unit tests over parsing and SQL-building helpers (`IsoWeek`, `SqlValuesBuilder`, `PackageDataChecker`, `DataDogRumIdentifiedUsers`, the response DTOs) and touch neither the network nor the database, so there is no reason not to run them. There is no test coverage of the activities or the workflow itself.

CI is two workflows: [build.yml](.github/workflows/build.yml) restores, builds and tests on every push and PR to `main`, and [run-workflow.yaml](.github/workflows/run-workflow.yaml) does the weekly collection.

## Layout

```
CollectDaprStats/        the service: workflows, activities, helpers
CollectDaprStats.Tests/  xUnit tests (InternalsVisibleTo from the main project)
resources/               Dapr components: postgres binding, secret store, workflow state store
postgres/                schema and one-off migration scripts (.psql)
docs/superpowers/        design specs and implementation plans, one per feature, dated
grafana/                 dashboard export
scripts/                 install-dapr.sh, scarf-probe.ps1
local-tests.http         the canonical way to drive the workflow by hand
```

## Architecture

`Program.cs` builds a minimal host and calls the parameterless `AddDaprWorkflow()`. **Workflows and activities are never registered explicitly** — there is no `RegisterWorkflow`/`RegisterActivity` call anywhere in the repo, so a new `WorkflowActivity<,>` subclass is picked up with no wiring. Constructor-injected dependencies do need registering in `Program.cs`.

`CollectorWorkflow` is the orchestrator. It reads a `CollectorWorkflowInput` and fans out to one activity per data source; `GitHubCollectorWorkflow` runs as a child workflow because the dapr org has many repositories. Each activity is a `WorkflowActivity<TInput, bool>` that fetches from one upstream API and writes to one table.

Three Dapr components carry the infrastructure:

- **`daprstats`** (`bindings.postgresql`) — every write and read goes through it, wrapped by `PostgresOutput.InsertAsync` / `ReadAsync`. There is no EF Core and no direct Npgsql usage; SQL is written by hand with `$1`-style positional parameters.
- **`secretstore`** (`secretstores.local.env`) — secrets are read from the process environment. `dapr.yaml` forwards the seven variables the code needs.
- **`workflowstore`** (`state.in-memory`) — **workflow state does not survive a restart.** A run interrupted halfway cannot be resumed and must be started again from the beginning.

## Adding a collector

1. Add `Get<Source>Data.cs` with a `WorkflowActivity<TInput, bool>` subclass. The input record's last parameter is `bool SkipStorage`, by convention.
2. Guard every `InsertAsync` with `if (!input.SkipStorage)`. This is what makes dry runs safe.
3. Add the list or flag to `CollectorWorkflowInput` in `CollectorWorkflow.cs` and guard the call site — lists with `.Length > 0`, flags with the bool. An empty list must mean "skip this source"; the GitHub Actions checkboxes rely on it. List entries can encode compound data (e.g. `service|env` pairs split on `|` in the workflow like `DockerHubImages` splits on `/`); guard with `?.Length > 0` when a payload field may be omitted entirely.
4. Keep the workflow class deterministic: no `DateTime.Now`, no `Guid.NewGuid()`, no HTTP calls in `RunAsync`. `CollectionDate` is passed in from outside for exactly this reason, and all I/O belongs in activities. Use `context.CreateTimer`, never `Task.Delay`.
5. Update all four of: `local-tests.http`, the `FIXED_*`/`DEFAULT_*` env values and inputs in `run-workflow.yaml`, the README data-source list, and `postgres/postgres_schema.psql`.

## Gotchas

These are all verified in the current code, not guesses:

- **`env` is a hostname on `conductor-ui`, not `prod`.** The `dev-dashboard`
  collector filters `env:prod`; that matches nothing on `conductor-ui`, whose
  `env` tag carries the deployment host (`conductor.r1.diagrid.io`,
  `dapr-ops-dashboard.diagrid.io`, and staging/local variants). This is why the
  identified-user collector takes `service|env` pairs rather than just a service
  name. Note also that `dapr-ops-dashboard.diagrid.io` reports under the
  `conductor-ui` service.
- **`@usr.anonymous_id` is not an identity on `conductor-ui`.** It is roughly
  per-session there (1411 sessions produced 1020 ids), unlike on
  `dev-dashboard`. Identified-user counting uses `@usr.id`.
- **`nuget_dapr_client` is a misnomer.** That one table holds all nine NuGet packages, distinguished by the `package_name` column.
- **`PostgresOuput.cs`** is misspelled on disk; the class inside is `PostgresOutput`.
- **Scarf's owner slug is case-sensitive.** `https://api.scarf.sh/v3/insights/Dapr/...` — the v3 endpoints return `404 Organization not found` for `dapr`. The comment in `GetScarfBuildingBlockViews.cs` says as much.
- **Scarf writes are delete-then-insert per ISO week**, which is what makes re-running a week idempotent. Any change to that write path needs the same guarantee.
- **A full run is slow by design.** The package loops retry up to three times with a five-minute backoff between attempts, so collection can take ~25 minutes. The CI job polls with a 25-minute deadline inside a 30-minute job timeout; anything that lengthens the retry path needs both raised.
- **`workflow_dispatch` allows at most 10 inputs.** `run-workflow.yaml` is exactly at the cap, which is why the Docker Hub images, Datadog services and Scarf pixel IDs are `FIXED_*` env values behind checkboxes rather than editable fields.
- **An emptied text input comes back as its default.** GitHub substitutes the `default:` when a `workflow_dispatch` text input is submitted empty, so a cleared field cannot be told apart from an omitted one. This once caused a manual run to collect every package after the fields had deliberately been emptied, duplicating a day of package data. The package inputs therefore take `all` / `none` / an explicit list, since `none` is a value GitHub cannot override. Boolean inputs are not affected: an unchecked box submits a real `false`.
- **Input defaults do not apply to scheduled runs at all.** GitHub leaves `inputs.*` empty for `schedule`, which the `all` keyword handles: empty resolves the same way as `all`, to the `DEFAULT_*` env value.

## Database

Neon Postgres 16, project `spring-pine-41263944`, database `daprstats`. The schema lives in `postgres/postgres_schema.psql`.

`datadog_rum_identified_users` is the only table here holding PII: real customer
email addresses and names. **This repository is public, so GitHub Actions run
logs are world-readable — never print or log a `@usr.id`, `@usr.email` or
`@usr.name` value.** Report from `datadog_rum_identified_users_view`, which
exposes counts. Deletion for an erasure request is
`DELETE FROM datadog_rum_identified_users WHERE user_email = $1`, but it only
sticks once that user falls outside the three-week lookback.
`datadog_rum_identified.unique_user_count` is a DataDog `cardinality` estimate,
exact at current volume, so a small divergence from
`datadog_rum_identified_users_view`'s registry-based count is expected at
higher volume, not a defect.

There is no `psql` on this machine and the Neon CLI has no SQL subcommand, so run queries over Neon's HTTP endpoint:

```
POST https://<endpoint-host>/sql
  -H "Neon-Connection-String: <conn string>"
  -H "Content-Type: application/json"
  -d '{"query": "...", "params": []}'
```

Get the connection string with `neon connection-string --project-id spring-pine-41263944 --role-name marcduiker --database-name daprstats` (the project has two roles, so `--role-name` is required). One statement per request, so no multi-statement transactions.

**This is the production database.** Never apply DDL without asking first. To check that a migration compiles and type-checks against the live schema without changing anything, wrap it in a `DO` block that raises at the end:

```sql
DO $$
BEGIN
  CREATE OR REPLACE VIEW ... ;
  RAISE EXCEPTION 'DRY RUN OK - rolling back';
END $$;
```

The exception aborts the statement, so nothing persists. `P0001 DRY RUN OK` means every statement succeeded; any other SQLSTATE is a real failure.

## Secrets

Seven environment variables, listed in the README and forwarded by `dapr.yaml`: `POSTGRESQLCONNECTION`, `DAPRSTATSGITHUBPAT`, `DISCORDBOTTOKEN`, `DAPRDISCORDSERVERID`, `DATADOGAPIKEY`, `DATADOGAPPKEY`, `SCARF_DAPR_API_TOKEN`. In CI they come from repository secrets of the same name.

`CollectDaprStats/secrets.json` holds real credentials. It is gitignored and untracked — never commit it, never print its contents, and never copy values out of it into a file, a commit message or a tool call.

## Git

Do not run `git add`, `git commit`, `git push`, or any other state-changing git command unless the maintainer asks for that specific operation in the current conversation. Read-only commands (`status`, `diff`, `log`, `show`) are fine. This mirrors the maintainer's standing instruction and applies to skill-driven workflows that would otherwise commit on their own.

## Planning docs

Features here are specced before they are built. `docs/superpowers/specs/` holds the design and `docs/superpowers/plans/` the implementation plan, both named `YYYY-MM-DD-<feature>`. The three most recent — package collection retry, Datadog RUM collection, Scarf event collection — are worth reading before touching those areas.

## Known planned work

Scarf collection supports one account (Dapr). Adding a second needs more than another token: the owner slug is a hardcoded `const` in both Scarf activities, the secret key is a compile-time `const`, and neither `scarf_building_block_views` nor `scarf_company_views` has an account column — their unique constraints would silently merge two accounts' rows, and the per-week delete would have each account wiping the other's data.
