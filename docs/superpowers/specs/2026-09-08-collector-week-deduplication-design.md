# Collector week deduplication — design

Make a second CollectorWorkflow run in the same ISO week update the stored
measurement instead of appending a duplicate row, for the seven sources that
have no such guard today.

## Problem

The workflow's append-only collectors write one row per run. Nothing stops two
runs in the same day, or the same week, from storing the same measurement
twice. This has now happened twice in eight days:

- **2026-09-01.** A manual dispatch at 17:21 UTC re-collected every package
  already collected that morning, because GitHub substitutes a text input's
  default when the field is submitted empty. 264 rows removed by
  `postgres/remove-duplicate-package-collection-2026-09-01.psql`.
- **2026-09-07.** A manual dispatch at 13:11 UTC, then the *scheduled* run,
  which GitHub created 6h10m late at 14:53 instead of 08:43. 316 rows removed
  by `postgres/remove-duplicate-collection-2026-09-07.psql`.

The second incident is the important one, because the input-validation fix
from the first did nothing to prevent it. The trigger was not operator error
but GitHub's scheduler: a delayed run looks skipped, someone dispatches
manually, and the delayed run then lands anyway. Measured queue-to-start and
creation delays on this workflow:

| scheduled run | delay |
|---|---|
| 2026-08-16 | started 2026-08-24 — 8 days |
| 2026-06-16 | 19h 39m |
| 2026-09-07 | creation slipped 6h 10m |
| 2026-07-01, 2026-07-16 | ~2h each |

No amount of care with workflow inputs fixes this. As long as duplication is
prevented only by runs not overlapping, it recurs.

Duplicates are not merely extra rows. `nuget_dapr_client_view` and
`dockerhub_images_view` compute `days_diff` between consecutive collections and
normalise with `/ NULLIF(days_diff,0) * 7`. The two 09-07 `daprio/daprd` rows
sat 1h32m50s apart — 0.0645 days — so the multiplier was 7/0.0645 ≈ **108×**.
The duplicate's 1,809-pull sliver would surface as an `avg_weekly_diff` near
196,000 against a true ~46,000, while the genuine row's delta was truncated to
that same sliver. Healthy history reads `days_diff` 6.3–23.1 and
`avg_weekly_diff` 41k–51k.

## Goal

A database constraint, not a timing assumption, guarantees at most one row per
entity per ISO week for these seven tables:

| table | rows (2026-09-08) |
|---|---|
| `nuget_dapr_client` | 6317 |
| `github_dapr` | 2776 |
| `npm_dapr_dapr` | 1132 |
| `python_dapr` | 145 |
| `dockerhub_images` | 144 |
| `discord_dapr` | 73 |
| `diagrid_dashboard` | 24 |

Re-running the workflow becomes the repair tool: a run that half-failed is
fixed by running it again, and two concurrent runs are safe for the first time.

## Non-goals

- **History is not normalised.** The irregular cadence stays as recorded. This
  prevents future duplication; it does not backfill.
- **The uncovered-days problem is untouched.** `github_dapr` and
  `npm_dapr_dapr` use hard-coded rolling windows (`CollectionPeriodInDays = 7`;
  npm's `CollectedOverNumberOfDays: 7`), so the ~96 npm days and ~24 GitHub
  days never collected stay uncollected, and a future gap longer than the
  window still loses activity permanently. Different bug, different fix.
- **Scarf and DataDog are not converted.** They are already idempotent per ISO
  week via delete-then-insert. See "Known limitations".
- **`nuget_dapr_client_view`'s stale `LAG` default** (`collection_date -
  INTERVAL '15 days'`, a leftover from the twice-monthly cadence, now wrong by
  a factor of two) is left alone. One-line fix, unrelated.

## Verified facts

All checked against the live Neon database on 2026-09-08. Nothing was
committed; every DDL probe ran inside an aborting `DO` block per
`postgres/`'s convention.

1. **A generated column works here.** `date_trunc` on a `timestamp without
   time zone` is `IMMUTABLE`, so Postgres accepts
   `GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED`.
   (It would be rejected for `timestamptz`, where `date_trunc` is only
   `STABLE`. Every one of these columns is `TIMESTAMP` without zone.)
2. **Existing rows backfill on `ADD COLUMN`.** No `UPDATE`, no `SET NOT NULL`
   step. Probed on `dockerhub_images`: id 145 came out as `2026-09-07`.
3. **The upsert updates rather than inserts.** Same probe: a second write for
   `(2026-09-07, daprio, daprd)` left the table at 144 rows, delta 0, one row
   affected, `pull_count` replaced.
4. **All seven collectors write one row per statement.** `SqlValuesBuilder`
   and its chunking are used only by the three Scarf collectors; each of the
   seven in scope makes a single single-row `InsertAsync`, inside a `foreach`
   for NuGet and npm. This is why no deduplication helper is needed: with one
   row per statement, `21000 ON CONFLICT DO UPDATE command cannot affect row a
   second time` is structurally unreachable. Two single-row upserts of the same
   key in one run simply have the second update the first.

   Multi-row upserts were probed anyway, in case these collectors are ever
   chunked: on `npm_dapr_dapr`, 15 rows upserted, table flat at 1132, delta 0,
   affected 15. The composition works, it is just not needed today.
5. **All fourteen DDL statements apply cleanly**, on every table except the
   `python_dapr` case below. Row counts flat through the whole probe: nuget
   6317→6317, github 2776→2776, discord 73→73, dashboard 24→24. The
   `github_dapr` upsert — the widest update list, twelve measurement columns —
   affected exactly one row.
6. **Exactly one historical collision exists**, and it is in `python_dapr`:

   ```
   23505  could not create unique index "python_dapr_week_unique"
          Key (collection_week, package_name)=(2026-07-27, dapr) is duplicated.
   ```

   | id | collection_date | download_count |
   |---|---|---|
   | 152 | 2026-07-29 13:23:32 | 358,005 |
   | 153 | 2026-08-01 10:25:09 | 406,398 |

   id 152 is the 07-29 partial retry; id 153 the 08-01 full run. The other six
   tables take the constraint with no violation.
7. **No run has ever emitted the same key twice.** Checked across all
   historical rows of `nuget_dapr_client`, `npm_dapr_dapr`, `python_dapr`,
   `github_dapr` and `dockerhub_images`: zero cases of one entity key
   appearing twice within a single collection day.
8. **The retry loop is unaffected.** `PackageDataChecker.FindMissing` filters
   `*_latest_packages_view` rows to `collection_date == today`. Under upsert a
   package that succeeds takes today's timestamp and reads as stored; one that
   failed keeps its older timestamp and reads as missing, so it is retried. If
   nothing stored today, all read missing and all are retried — identical to
   current behaviour. The loop is bounded at `MaxAttempts = 3` with a 5-minute
   `RateLimitBackoff`.

## Architecture

Two changes, one per layer.

**Database:** each of the seven tables gains a generated `collection_week`
column and a `UNIQUE` constraint over `(collection_week, <entity key>)`.

**Application:** a new `UpsertBuilder` generates the `ON CONFLICT ... DO UPDATE`
clause; the seven collectors append it to the SQL they already build.

### This is the pattern the repo already uses

Every table that is already safe against re-runs carries a unique constraint;
the eight that exist today are:

| table | constraint | key columns |
|---|---|---|
| `datadog_rum` | `datadog_rum_service_week_unique` | `service, week_start` |
| `datadog_rum_identified` | `datadog_rum_identified_week_unique` | `service, env, week_start` |
| `datadog_rum_identified_user_orgs` | `..._unique` | `service, env, user_id, organization` |
| `datadog_rum_identified_users` | `..._unique` | `service, env, user_id` |
| `datadog_rum_users` | `datadog_rum_users_service_user_unique` | `service, user_anonymous_id` |
| `scarf_building_block_views` | `..._unique` | `week_start, building_block, company_name, company_domain` |
| `scarf_company_views` | `..._unique` | `week_start, company_name, company_domain` |
| `scarf_diagrid_page_views` | `..._unique` | `week_start, site, page_path, company_name, company_domain` |

So this design is not novel — it extends an established pattern to the seven
tables that were never brought into it. The proposed constraint names
(`<table>_week_unique`) follow the existing convention set by
`datadog_rum_service_week_unique`.

The seven tables in scope are precisely the set with **no** unique constraint
at all, which is why they are the only ones that ever duplicated.

### Why an ISO week and not a calendar day

A day-grain key still permits two rows in one week on different days, which is
precisely the shape of the real history — 2026-07-29 and 2026-08-01, or
2026-08-01 and 2026-08-24. The scheduler's delays are measured in hours to
days, so the deduplication window has to be at least as wide as the delay.

### Why the column is generated, not application-populated

A plain column would need the app to compute the week and pass it on every
insert, which makes a row whose `collection_week` disagrees with its
`collection_date` expressible. Generated makes that state unrepresentable,
removes a parameter from all seven inserts, and backfills history for free.

### Why it is called `collection_week` and not `week_start`

`week_start` already means "the week this data describes" in
`scarf_*` and `datadog_rum*`. It cannot mean that here: these tables mix
cumulative point-in-time snapshots (Docker pull totals, Discord member counts)
with backward-looking rolling windows, so a Monday run's GitHub numbers mostly
describe the *previous* week. `collection_week` means only "the week the run
happened in" — a deduplication slot, nothing more. The different name is the
guard: joining these tables to `scarf_company_views.week_start` would be off by
roughly a week, and the name says so rather than letting the join look correct.

### Why last-write-wins

`DO UPDATE` makes re-running the repair tool. A run that stores 0 commits for a
repo because GitHub throttled it is corrected by running again. `DO NOTHING`
would freeze the bad number for the week, recoverable only by a manual
`DELETE`. Last-write-wins also matches what Scarf and DataDog already do, so
one rule covers every table.

The cost is that `github_dapr`'s rolling 7-day window shifts to the later run's
timestamp. `collected_over_number_of_days` records the window and the views
normalise by it, so this is visible rather than silent.

### Why a shared builder and not seven inline clauses

`SqlValuesBuilder` already exists in this repo as "a separate, tested unit
rather than string concatenation at the call site". The argument is stronger
here: `github_dapr` has twelve measurement columns, and a hand-typed
`SET commit_count = EXCLUDED.comment_count` would corrupt data silently and
permanently. Seven inline clauses means ~35 column mappings testable only
against the live binding.

## Components

### `UpsertBuilder` (new)

Sibling of `SqlValuesBuilder`, same style: exact-string output, no database.

```csharp
UpsertBuilder.BuildOnConflict(
    keyColumns:    ["collection_week", "namespace", "image_name"],
    updateColumns: ["collection_date", "pull_count"])

// "on conflict (collection_week,namespace,image_name) do update set
//  collection_date=excluded.collection_date,pull_count=excluded.pull_count"
```

Throws on the four ways to get this wrong, each a test:

- empty `keyColumns`
- empty `updateColumns`
- a column in both lists — updating a key column
- `collection_week` in `updateColumns`. Postgres rejects writing a generated
  column anyway; failing in a unit test beats failing in production.

Column order is preserved as given, so golden-string tests are stable.

### No deduplication helper

An earlier draft of this design included a `DeduplicateByKey` helper to guard
against one statement carrying the same key twice, which Postgres rejects with
`21000 ON CONFLICT DO UPDATE command cannot affect row a second time`.

It is not needed. Verified fact 4: every one of the seven collectors writes one
row per statement, so the error is unreachable. Two single-row upserts of the
same key within a run have the second update the first, which is exactly the
wanted behaviour. Dropped rather than carried as dead insurance.

If any of these collectors is ever converted to chunked multi-row inserts, the
guard becomes necessary — noted in "Known limitations".

### The seven collectors (extended)

Each gains two static column arrays and one string append. `INSERT` column
lists are unchanged — `collection_week` is generated, so nothing inserts it.

| collector | key columns | update columns |
|---|---|---|
| `GetNuGetPackageData` | `collection_week, package_name, package_version` | `collection_date, download_count` |
| `GetNpmPackageData` | `collection_week, package_name, package_version` | `collection_date, download_count, collected_over_number_of_days` |
| `GetPythonPackageData` | `collection_week, package_name` | `collection_date, package_version, download_count, collected_over_number_of_days` |
| `GetGitHubRepoData` | `collection_week, repo_name` | `collection_date` + the 12 measurement columns |
| `GetDockerHubData` | `collection_week, namespace, image_name` | `collection_date, pull_count` |
| `GetDiscordData` | `collection_week` | `collection_date, member_count` |
| `GetDiagridDashboardData` | `collection_week` | `collection_date, download_count` |

`github_dapr`'s twelve: `fork_count_total`, `star_count_total`,
`commit_count`, `commit_users`, `issue_count`, `issue_users`, `comment_count`,
`comment_users`, `pullrequest_count`, `pullrequest_users`,
`distinct_user_count`, `collected_over_number_of_days`.

## Database schema

Per table, two statements. `dockerhub_images` shown; the other six follow the
same shape with their own key columns.

```sql
ALTER TABLE dockerhub_images
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;

ALTER TABLE dockerhub_images
  ADD CONSTRAINT dockerhub_images_week_unique
  UNIQUE (collection_week, namespace, image_name);
```

`discord_dapr` and `diagrid_dashboard` take a single-column
`UNIQUE (collection_week)` — one row per week, no entity dimension.

The unique constraints create their own indexes. The largest table is 6317
rows, so index build cost is negligible and no separate index is needed.

## Write strategy

Unchanged in shape: one single-row statement per invocation through the Dapr
Postgres binding, now with an `ON CONFLICT` tail. NuGet and npm issue one such
statement per package version inside a `foreach`, so a NuGet run is ~248
sequential round trips before and after this change.

The binding has no transaction, so a mid-run failure leaves some entities
updated and some not. Last-write-wins makes that self-repairing: run it again.
This is strictly better than delete-then-insert, where a crash after the
`DELETE` loses the week outright.

Two concurrent runs are safe. Concurrent upserts of the same key serialise in
Postgres; no duplicate row is possible. The guarantee moves from the workflow's
timing into a constraint, which is the actual root cause of both incidents.

## Error handling

Unchanged. A unique violation is no longer reachable from the new code path —
the conflict is handled, not raised. The collectors keep their existing
per-source exception handling and `bool` returns.

## Testing

Mirroring `SqlValuesBuilderTests`' exact-string style, in
`CollectDaprStats.Tests` (which already reaches internals via
`InternalsVisibleTo`):

- `UpsertBuilderTests` — clause shape for single and multi-column keys; the
  four rejections.
- No deduplication-helper tests — the helper was dropped. See "No
  deduplication helper".
- **Golden-string test per collector** — asserts the full composed
  `INSERT ... VALUES ... ON CONFLICT ...`. This is the test that catches a
  transposed `EXCLUDED` column, and the reason a shared builder was chosen
  over inline clauses.

## Verification

1. Dry-run every migration statement in an aborting `DO` block. **Done for all
   seven tables** (verified facts 1–6); each returned `P0001 DRY RUN OK` with
   nothing persisted, except `python_dapr`, which returned the expected `23505`
   until id 152 is resolved. Re-run these after any edit to the migration file.
2. Apply the migration, then confirm `information_schema.columns` shows seven
   `collection_week` columns and `information_schema.table_constraints` the
   seven unique constraints.
3. Deploy, then run the workflow twice in the same week. Confirm row counts are
   flat across the second run and that `collection_date` advanced on the
   affected rows.
4. Confirm `dockerhub_images_view.days_diff` for the affected week stays in the
   6–8 range rather than collapsing toward zero.

## Migration

One file, `postgres/add-collector-week-dedup-2026-09-08.psql`, following the
house pattern: header rationale, preview, statements, verification.

Order matters and the steps must not be separated:

1. **Resolve the one collision first.** `DELETE FROM python_dapr WHERE id =
   152`. This discards a real measurement (`dapr`, 358,005 downloads,
   2026-07-29); both values are recorded in "Verified facts" above so it is
   recoverable. Approved by the maintainer on 2026-09-08.
2. **Apply the fourteen DDL statements.**
3. **Deploy the code.**

Deploying before step 2 breaks every insert immediately, since `ON CONFLICT
(collection_week, ...)` would reference a column that does not exist. So the
migration leads. That leaves a window in which the *old* code's plain `INSERT`
meets the new constraint on a second same-week run and raises a unique
violation instead of duplicating — a loud failure rather than a silent one, and
bounded by `MaxAttempts = 3`, but it would burn a run. Apply and deploy in the
same sitting.

Neon's HTTP endpoint takes one statement per request, so the file is written
for the Neon SQL editor with statements separate rather than as one
transaction.

## Documentation changes

- `README.md` — note that collection is idempotent per ISO week, so a re-run
  repairs a partial run rather than duplicating it. This replaces the current
  emphasis on typing `none` to avoid double collection, which addressed only
  the 09-01 shape of the bug.
- `postgres/postgres_schema.psql` — add the seven columns and constraints, with
  a comment on `collection_week` stating it is a deduplication slot and
  explicitly not the same thing as `week_start`.

## Known limitations

- **Scarf and DataDog keep delete-then-insert.** A crash between a week's
  `DELETE` and its last `INSERT` leaves that week short until the next run's
  three-week overlap repairs it. Converting them to upsert would remove that
  weakness but is not a straight swap: Scarf's company attribution is
  retroactive, so a re-attributed row's old key must be deleted or it lingers
  as a phantom forever. Out of scope.
- **A skipped week is still a hole.** Deduplication guarantees at most one row
  per week, never at least one. Nothing here detects or backfills a week the
  workflow never ran, and for `github_dapr` and `npm_dapr_dapr` the underlying
  activity is unrecoverable once the rolling window has passed.
- **Chunking these collectors would reintroduce a hazard.** The seven write
  one row per statement today, which is why no deduplication guard is needed.
  If one is ever converted to chunked multi-row inserts for round-trip cost —
  a NuGet run is ~248 sequential statements — the chunk must be deduplicated by
  key first, or Postgres rejects it with `21000`.
- **`collection_week` uses Postgres' `date_trunc('week', ...)`**, which is
  ISO-8601 Monday-based and independent of the `DateStyle` setting. The
  application's `IsoWeek` helper agrees, but the two are not shared code — the
  column is computed entirely in the database.
