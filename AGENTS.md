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

The test suite is 186 xUnit tests that run in about 100 ms. They are pure unit tests over parsing and SQL-building helpers (`IsoWeek`, `SqlValuesBuilder`, `PackageDataChecker`, `DataDogRumIdentifiedUsers`, the response DTOs) and touch neither the network nor the database, so there is no reason not to run them. There is no test coverage of any activity's `RunAsync` or of the workflow itself, but pure helpers extracted from activities — such as `GetScarfPageViews.Aggregate` and `GetScarfPageViews.MissingSites` — are covered.

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
- **`secretstore`** (`secretstores.local.env`) — secrets are read from the process environment. `dapr.yaml` forwards the eight variables the code needs.
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
- **RUM user attributes are named `@usr.organization` / `@usr.organizationName`**,
  not `@usr.organization_id` or `@usr.tenant` — those do not exist and return
  nothing, which once led to a spec claiming there was no organisation dimension
  at all. Enumerate the real field names with
  `ddsql_schema_search_unstructured_fields` on `public.dd.rum` rather than
  guessing. `@usr.organization` is well populated; `@usr.organizationName` is
  only partly populated and is not a reliable label.
- **`@usr.anonymous_id` is not an identity on `conductor-ui`.** It is roughly
  per-session there (1411 sessions produced 1020 ids), unlike on
  `dev-dashboard`. Identified-user counting uses `@usr.id`.
- **`@usr.organization` is per-session and many-to-many.** A Conductor user can
  belong to several organisations, and the attribute records the organisation
  context of the *session*, not of the account: one production account has been
  seen under 8 organisations and four more under 2. This is why
  `datadog_rum_identified_user_orgs` exists, and why
  `datadog_rum_identified_users.user_organization` must never be counted -- it
  keeps one value per user and returned 17 distinct organisations where DataDog
  had 24. Watch the group-by limit too: the `orgs` request needs its own
  `OrgGroupByLimit` (50) rather than the `AttributeGroupByLimit` (5) that
  `emails` and `names` use, because 5 truncates the 8-organisation account under
  the per-parent reading of DataDog's 10,000-groups cap.
- **`nuget_dapr_client` is a misnomer.** That one table holds all nine NuGet packages, distinguished by the `package_name` column.
- **`PostgresOuput.cs`** is misspelled on disk; the class inside is `PostgresOutput`.
- **Scarf's owner slug is case-sensitive, and there are now two accounts.**
  `Dapr` and `Diagrid`; the v3 endpoints return `404 Organization not found` for
  a lowercased slug. The slug and the token secret name are no longer consts on
  a shared URL — each activity declares its own `Owner` and `ApiTokenSecret` and
  passes them to `ScarfExportClient`. Diagrid page rows live in
  `scarf_diagrid_page_views`, separate from the two Dapr tables, so neither
  table needs an `account` column yet.
- **`ScarfExportRequest.GroupByArtifact` is `bool?` on purpose.** Omitting
  `group_by_artifact` and sending `group_by_artifact=true` are different
  requests. `GetScarfCompanyViews` sends `false` to merge its two pixels;
  `GetScarfBuildingBlockViews` and `GetScarfPageViews` omit it. Do not collapse
  it to a plain `bool`.
- **Scarf writes are delete-then-insert per ISO week**, which is what makes re-running a week idempotent. Any change to that write path needs the same guarantee.
- **A full run is slow by design.** The package loops retry up to three times with a five-minute backoff between attempts, so collection can take ~25 minutes. The CI job polls with a 25-minute deadline inside a 30-minute job timeout; anything that lengthens the retry path needs both raised.
- **`workflow_dispatch` allows at most 10 inputs.** `run-workflow.yaml` is exactly at the cap, which is why the Docker Hub images, Datadog services and Scarf pixel IDs are `FIXED_*` env values behind checkboxes rather than editable fields.
- **An emptied text input comes back as its default.** GitHub substitutes the `default:` when a `workflow_dispatch` text input is submitted empty, so a cleared field cannot be told apart from an omitted one. This once caused a manual run to collect every package after the fields had deliberately been emptied, duplicating a day of package data. The package inputs therefore take `all` / `none` / an explicit list, since `none` is a value GitHub cannot override. Boolean inputs are not affected: an unchecked box submits a real `false`.
- **Input defaults do not apply to scheduled runs at all.** GitHub leaves `inputs.*` empty for `schedule`, which the `all` keyword handles: empty resolves the same way as `all`, to the `DEFAULT_*` env value.

## Database

Neon Postgres 16, project `spring-pine-41263944`, database `daprstats`. The schema lives in `postgres/postgres_schema.psql`.

`datadog_rum_identified_users` and `datadog_rum_identified_user_orgs` are the
tables here holding PII: real customer email addresses, names and organisation
identifiers. **This repository is public, so GitHub Actions run
logs are world-readable — never print or log a `@usr.id`, `@usr.email` or
`@usr.name` value.** Report from `datadog_rum_identified_users_view`, which
exposes counts. Deletion for an erasure request is two statements, because the link table keys
on `user_id` and not on the email:
`DELETE FROM datadog_rum_identified_user_orgs WHERE (service, env, user_id) IN
(SELECT service, env, user_id FROM datadog_rum_identified_users WHERE user_email = $1)`
first, then `DELETE FROM datadog_rum_identified_users WHERE user_email = $1`.
Doing it the other way round leaves the link rows unreachable. Either way it
only sticks once that user falls outside the three-week lookback.
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

Eight environment variables, listed in the README and forwarded by `dapr.yaml`: `POSTGRESQLCONNECTION`, `DAPRSTATSGITHUBPAT`, `DISCORDBOTTOKEN`, `DAPRDISCORDSERVERID`, `DATADOGAPIKEY`, `DATADOGAPPKEY`, `SCARF_DAPR_API_TOKEN`, `SCARF_DIAGRID_API_TOKEN`. In CI they come from repository secrets of the same name.

`CollectDaprStats/secrets.json` holds real credentials. It is gitignored and untracked — never commit it, never print its contents, and never copy values out of it into a file, a commit message or a tool call.

## Git

Do not run `git add`, `git commit`, `git push`, or any other state-changing git command unless the maintainer asks for that specific operation in the current conversation. Read-only commands (`status`, `diff`, `log`, `show`) are fine. This mirrors the maintainer's standing instruction and applies to skill-driven workflows that would otherwise commit on their own.

## Planning docs

Features here are specced before they are built. `docs/superpowers/specs/` holds the design and `docs/superpowers/plans/` the implementation plan, both named `YYYY-MM-DD-<feature>`. The three most recent — Scarf Diagrid page collection, the identified-user/organisation many-to-many fix, and Datadog identified-user collection — are worth reading before touching those areas.

## Known planned work

Scarf collection now covers two accounts, Dapr and Diagrid, each activity carrying its own `Owner` and `ApiTokenSecret` into `ScarfExportClient`. What's still outstanding: `scarf_building_block_views` and `scarf_company_views` have no account column, so those two tables remain Dapr-only. That hasn't bitten anything because Diagrid rows live in their own `scarf_diagrid_page_views` table, but a future account writing to either of the original two would need that column added, folded into the unique constraint and into the per-week DELETE, or the accounts would silently merge and overwrite each other.
