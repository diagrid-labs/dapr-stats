# Scarf Java package collection — design

Date: 2026-09-15

## Problem

Java is the only SDK ecosystem still collected by hand. [java-import.md](../../../postgres/java-import.md)
describes the ritual: export a CSV per month from Sonatype, open it in a text
editor, add three constant columns to every row, import through pgAdmin.

It has not been done since April 2025. `java_dapr_sdk` holds 1,065 rows spanning
2023-12 to 2025-04, all for a single package — `dapr-sdk`. The other Java
artifacts have never been collected at all.

Scarf already tracks every one of them under the Dapr account, on the same v3
export endpoint and the same `SCARF_DAPR_API_TOKEN` the pixel collectors use.

## Goals

1. Collect JVM package downloads weekly and automatically, per version,
   alongside the other package ecosystems in the Monday run.
2. Cover every `io.dapr` artifact Scarf knows about, including ones registered
   later, without a code change.
3. Add no wall-clock time to the run.
4. Free a `workflow_dispatch` input slot, since `run-workflow.yaml` sits at
   GitHub's cap of 10.

## Non-goals

- **Backfilling 2025-04 to 2026-09.** The v3 window is capped at 366 days and
  Scarf's history does not reach the gap. It stays a hole.
- **Migrating or rewriting `java_dapr_sdk`.** It is frozen as the Sonatype
  archive. Different cadence, different naming, different source.
- **Company attribution for packages.** `by-company` is available on this
  endpoint but is not collected here; the leaderboard is a pixel concern and a
  second dimension multiplies the row count.
- **Gap detection, retry and reporting.** That is the
  [package collection gap handling design](2026-09-15-package-collection-gap-handling-design.md),
  developed separately on `spec-package-gap-handling`. This design deliberately
  does not anticipate it — see *Interaction with in-flight work*.

## What the API gives us

Verified against the live API on 2026-09-15 with `scripts/scarf-probe.ps1`,
not inferred from the OpenAPI document.

### The metric is an install event count

Scarf names the counter `total_installs` on `/v2/packages/{owner}/overview` and
`total` on a v3 export row. One event is one artifact fetch.

| Field | Meaning | Stored as |
|---|---|---|
| `total` | install events — the download count | `download_count` |
| `unique_origins` | distinct origin IPs behind those events | `unique_origins` |
| `unique_endpoints` | distinct Scarf routes hit | not stored — `1` on all 2,437 rows |

`unique_origins` is the one addition to the shape `npm_dapr_dapr` uses. It costs
a nullable column, npm and PyPI cannot provide anything like it, and it is
unrecoverable for past weeks if it is not captured as the weeks go by.

### The unit is (ISO week, package, version)

`rollup=weekly` returns Monday-anchored buckets and `end_date` is exclusive,
exactly as the pixel endpoints behave, so `IsoWeek` needs no adjustment.
`version` is populated on every row — 79 to 101 distinct versions per week for
`dapr-sdk`, matching the ~77 versions per month the Sonatype CSVs carried.
`artifact_name` is the full coordinate (`io.dapr/dapr-sdk`).

A probe row, null fields removed:

```json
{
  "date": "2026-08-24",
  "artifact": "0d1e80df-9a30-4b62-92e0-3814ce4bcffa",
  "artifact_name": "io.dapr.spring/dapr-spring-boot-autoconfigure",
  "artifact_type": "package",
  "rollup": "weekly",
  "breakdown": "by-version",
  "version": "1.16.1-rc-3",
  "total": 6,
  "unique_origins": 3,
  "unique_endpoints": 1
}
```

Zero duplicate `(date, artifact_name, version)` keys across 2,437 rows, so
unlike `GetScarfBuildingBlockViews` — which re-sums because its referer
normalisation is many-to-one — this collector needs no aggregation step.
Longest `version` is 15 characters, longest `artifact_name` 45. Volume is
~800 rows per week.

### `query=io.dapr*` selects exactly the JVM packages

A ten-week probe with `query=io.dapr*` returned 9,271 rows across exactly 18
artifacts and **zero** rows whose name does not match. The `Dapr CLI` artifact
that `package_id=all` pulls in is correctly excluded.

So one `query` parameter replaces eighteen `package_id` UUIDs, and a nineteenth
package is collected the week it is registered rather than the week someone
remembers to edit a list.

| Package | Installs, last month |
|---|---|
| `io.dapr/dapr-sdk` | 116,715 |
| `io.dapr/dapr-sdk-autogen` | 109,242 |
| `io.dapr/dapr-sdk-parent` | 66,333 |
| `io.dapr/dapr-sdk-actors` | 46,034 |
| `io.dapr/dapr-sdk-springboot` | 42,269 |
| `io.dapr/dapr-sdk-workflows` | 35,326 |
| `io.dapr/durabletask-client` | 32,601 |
| `io.dapr.spring/dapr-spring-messaging` | 20,296 |
| `io.dapr.spring/dapr-spring-workflows` | 19,105 |
| `io.dapr.spring/dapr-spring-parent` | 11,806 |
| `io.dapr.spring/dapr-spring-data` | 4,681 |
| `io.dapr/testcontainers-dapr` | 4,525 |
| `io.dapr.spring/dapr-spring-boot-autoconfigure` | 4,344 |
| `io.dapr.spring/dapr-spring-boot-starter` | 4,032 |
| `io.dapr.spring/dapr-spring-boot-tests` | 2,930 |
| `io.dapr.spring/dapr-spring-boot-starter-test` | 2,523 |
| `io.dapr/client` | 139 |
| `io.dapr/dapr-sdk-springboot3` | 112 |

The first ten are the originally requested list.

### Scarf ingests two to three days late

This is the finding that sets which week a Monday run collects. Every complete
week's `max(last_seen)` lands exactly on its Sunday — except the most recent:

| week_start | total | max(last_seen) | |
|---|---|---|---|
| 2026-07-06 | 185,098 | 2026-07-12 | Sunday |
| 2026-08-17 | 135,055 | 2026-08-23 | Sunday |
| 2026-08-24 | 160,974 | 2026-08-30 | Sunday |
| 2026-08-31 | 129,547 | 2026-09-06 | Sunday |
| 2026-09-07 | 94,057 | 2026-09-**12** | **Saturday** |

Probed on Tuesday 2026-09-15, the latest event Scarf has ingested anywhere is
Saturday the 12th. Sunday the 13th is absent entirely and Saturday is likely
partial.

Scaling 94,057 over ~5.5 of 7 days gives ~120k against the previous week's
129,547, so the real change is about −7%, not the −27% the raw figure shows.
Across ten weeks the series runs 185k down to ~130k with ordinary noise — a
genuine summer drift, not a cliff.

**A Monday run therefore collects the week before last**, not the week that
ended the previous day. One week per run, written once, never revised.

## Design

### Which week

```csharp
// [0] is the older of the two, i.e. the week before last. The most recently
// completed week is skipped: Scarf is still ingesting it.
var week = IsoWeek.CompleteWeeksBefore(input.CollectionDate, 2)[0];
```

For the scheduled Monday 08:43 UTC run this is the ISO week that ended eight
days earlier, which the `last_seen` evidence shows Scarf has finished ingesting.

`input.CollectionDate` rather than `DateTime.UtcNow`: it is passed in from
outside for exactly this reason, and it keeps the requested week stable if the
activity is retried within a run.

The cost is freshness — on Monday the newest row describes a week that ended
eight days ago. The alternative was storing a systematically ~25% understated
row every week, permanently, since one-week-per-run never revisits it.

### Fetch

A new activity `GetJavaPackageData : WorkflowActivity<JavaPackageInput, bool>`
in `CollectDaprStats/GetJavaPackageData.cs`, named to match
`GetNpmPackageData`. It is a fourth caller of the existing `ScarfExportClient`
and declares its own `Owner = "Dapr"` and `ApiTokenSecret = "SCARF_DAPR_API_TOKEN"`,
per the per-activity convention the two-account change established.

**One request covers all eighteen packages.** Unlike npm and PyPI, which need a
call per package and therefore the `Get → Check → Sleep` retry loop to survive
429s, Scarf answers the whole ecosystem in a single export. So there is no
per-package fan-out, no 5-second spacing timers, no `CheckJavaPackageData`, and
**no increase to the ~25-minute run** — neither the 30-minute job timeout nor
the 25-minute poll deadline moves.

`ScarfExportRequest` gains one field:

```csharp
string? Query,   // package-name DSL; mutually exclusive with PixelIds
```

emitted by `BuildUrl` as `&query=`, URL-escaped. The v3 spec says the
package-name query combines with neither `package_id` nor `tracking_pixel_id`,
so `BuildUrl` throws `ArgumentException` when `Query` and a non-empty `PixelIds`
are both set rather than letting Scarf answer 422 at runtime.

The request is `rollup=weekly`, `breakdown_set=by-version`, `format=json`, and
`GroupByArtifact: null`. The null matters: the parameter defaults to `true`
server-side, which is what yields one row per artifact. Omitting it preserves
the asymmetry `ScarfExportClient` already documents.

Failures are caught and reported as `false`, never rethrown — this runs
alongside other collectors under `Task.WhenAll`, and an exception would abandon
the GitHub collection below it. A response with zero rows is logged as a warning
and returns `false`. Because writes are upserts rather than delete-then-insert,
an empty response cannot destroy anything, so this needs none of the
elaborate pre-delete guard `GetScarfBuildingBlockViews` carries.

### Parse

`ScarfAggregationResponse` gets a second entry point:

```csharp
public sealed record PackageRow(
    DateOnly WeekStart,
    string PackageName,
    string Version,
    long Downloads,
    long UniqueOrigins);

public static IReadOnlyList<PackageRow> ParsePackageVersions(ReadOnlySpan<byte> json)
```

The existing `Parse` is left exactly as it is. Its skip of rows with a null
`company_name` is correct for the three pixel collectors and wrong only for this
breakdown — a by-version row has no company, so reusing `Parse` here would
return zero rows from a healthy response. The private `ReadWeekStart`,
`ReadString` and `ReadCount` helpers are shared; `ParsePackageVersions` skips
rows missing `artifact_name` or `version` on the same defensive grounds.

### Store

```sql
CREATE TABLE java_dapr (
    id SERIAL PRIMARY KEY,
    package_name VARCHAR(255) NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    package_version VARCHAR(100) NOT NULL,
    download_count BIGINT NOT NULL,
    collected_over_number_of_days SMALLINT,
    week_start DATE NOT NULL,
    unique_origins BIGINT,
    CONSTRAINT java_dapr_week_unique
      UNIQUE (week_start, package_name, package_version)
);
```

Columns follow `npm_dapr_dapr` — `package_name`, `collection_date`,
`package_version`, `download_count`, `collected_over_number_of_days` (always 7)
— with `week_start` and `unique_origins` added.

**`week_start`, not `collection_week`.** The
[deduplication migration](../../../postgres/add-collector-week-dedup-2026-09-08.psql)
is explicit that `collection_week` means only "the week the run happened in",
and warns that joining it to a `scarf_*.week_start` would be off by roughly a
week. Here the gap is deliberately two weeks, so conflating them would be worse
still. `week_start` is the name those tables already use for "the week this data
describes", and it is what this data is keyed by.

That also makes it the better conflict target. Keying on the run's week would
mean a run delayed across a week boundary — measured at two hours, nineteen
hours and once eight days on this workflow — could write a second copy of a week
it already held. Keying on `week_start` makes the collector idempotent per data
week no matter when it runs, which is what the deduplication design asks every
source to guarantee.

Writes are `insert ... on conflict ... do update set` via `UpsertBuilder`,
exactly as npm and Python do:

```csharp
private static readonly string[] KeyColumns =
    ["week_start", "package_name", "package_version"];

private static readonly string[] UpdateColumns =
    ["collection_date", "download_count", "unique_origins",
     "collected_over_number_of_days"];
```

Rows are chunked 500 to a statement with `SqlValuesBuilder` — 500 × 7 = 3,500
parameters, well inside Postgres' 65,535 limit, and ~800 rows a week means two
statements. npm inserts a row per round trip, which is tolerable for one package
and not for eighteen.

Composing `SqlValuesBuilder.Build(...)` with `UpsertBuilder.BuildOnConflict(...)`
in one statement is new — the Scarf collectors chunk without upserting and the
package collectors upsert without chunking — so it gets its own test.

Every `InsertAsync` is guarded by `if (!input.SkipStorage)`.

### Workflow wiring

`CollectorWorkflowInput` gains `string[] JavaPackageNames`, named to match its
three siblings. Entries are package-name DSL patterns joined with `,` (the DSL's
top-level alternation); the default is the single pattern `io.dapr*`. An
explicit coordinate such as `io.dapr/dapr-sdk` allows a single-package re-run.

It joins the concurrent package group, guarded by the empty-list convention.
The guard is `?.Length > 0`, not `.Length > 0`: existing payloads in
`local-tests.http` and any in-flight dispatch omit the field entirely, which
deserialises to null.

```csharp
if (input.JavaPackageNames?.Length > 0)
{
    packageLoops.Add(context.CallActivityAsync(
        nameof(GetJavaPackageData),
        new JavaPackageInput(
            input.JavaPackageNames, input.CollectionDate, input.SkipStorage)));
}
```

`packageLoops` is awaited by the existing `Task.WhenAll`, so Java collection
overlaps the three retry loops rather than extending them.

### CI inputs

`run-workflow.yaml` is at GitHub's cap of 10 `workflow_dispatch` inputs. The
three package text inputs merge into one, taking it to 8:

```yaml
packages:
  description: 'all | none | <ecosystem>:<list> groups, ; separated'
  required: false
  type: string
  default: 'all'
```

| Value | Meaning |
|---|---|
| `all` (or empty, as on a scheduled run) | every ecosystem at its `DEFAULT_*` list |
| `none` | no package collection at all |
| `java:all` | only the named ecosystems; **every unnamed one is skipped** |
| `nuget:Dapr.Client,Dapr.Jobs;java:all` | explicit per-package lists where wanted |

"Unnamed means skipped" is the conservative reading and is the point of the
syntax: a group string is written to re-run something specific, and quietly
collecting four other ecosystems alongside it is the failure that made `none` a
keyword. The `all`/`none` keywords survive at the top level for the reason they
exist today — an emptied text input comes back as its `default:`, so emptiness
can never mean "skip".

`DEFAULT_NUGET_PACKAGES`, `DEFAULT_NPM_PACKAGES` and `DEFAULT_PYTHON_PACKAGES`
are joined by `DEFAULT_JAVA_PACKAGES: 'io.dapr*'`. The `Generate Variables` step
gains a `resolve_group` helper beside the existing `resolve_list`, and the
payload gains `JavaPackageNames`.

This leaves two free input slots.

### Tests

New xUnit cases, consistent with the suite's rule that only pure helpers are
covered:

- `ScarfAggregationResponseTests`: `ParsePackageVersions` over a fixture cut
  from the probe dump — row count, week bucketing, version strings, counters,
  and that a row missing `artifact_name` or `version` is skipped. The fixture
  holds package and version names only, so unlike the RUM fixtures it carries no
  PII and can be committed.
- A regression case asserting the existing `Parse` still drops null-company
  rows, so the two paths cannot later be collapsed by accident.
- `ScarfExportClient.BuildUrl`: `query=` is emitted and escaped,
  `group_by_artifact` is absent when `GroupByArtifact` is null, and `Query`
  with a non-empty `PixelIds` throws.
- `CollectorInsertSqlTests`: the chunked-upsert statement — that
  `SqlValuesBuilder` placeholders and the `on conflict` tail compose into valid
  SQL with the conflict target matching `java_dapr_week_unique`.
- `IsoWeekTests`: `CompleteWeeksBefore(x, 2)[0]` is the week before last, for a
  Monday run and for a mid-week manual run.

### Schema and docs

- `postgres/postgres_schema.psql` — add `java_dapr`.
- `postgres/add-java-dapr-2026-09-15.psql` — the dated migration, dry-run first
  inside an aborting `DO` block per the house rule, applied only after explicit
  approval.
- `postgres/java-import.md` — note that the manual ritual is superseded from
  2026-09 and that `java_dapr_sdk` is a frozen archive.
- `local-tests.http` — a `JavaPackageNames` payload.
- `README.md` — add the data source.
- `AGENTS.md` — the `package_id=all` trap, Scarf's two-to-three-day ingestion
  lag and the week offset it forces, and that `ScarfAggregationResponse` now has
  two entry points.

## Interaction with in-flight work

The [package collection gap handling design](2026-09-15-package-collection-gap-handling-design.md)
lives on the `spec-package-gap-handling` worktree and is separate work. It
touches three of the same files, so whoever lands second should expect textual
conflicts:

- `CollectorWorkflow.cs` — that design changes `CollectorWorkflow` to
  `Workflow<CollectorWorkflowInput, CollectorWorkflowResult>` and the three
  loop helpers to `Task<string[]>`; this one adds a call into `packageLoops`.
- `CollectorWorkflowInput` — that design adds `bool GapFillOnly`, this one adds
  `string[] JavaPackageNames`. Both must also reach the JSON payload.
- `run-workflow.yaml` — both edit `Generate Variables`. The 10-to-8 input merge
  here frees the slot a `gap_fill_only` input would otherwise have nowhere to
  go.

Java is outside that design's scope by construction: it has no per-package loop
and no `Check` activity, because one request collects the whole ecosystem.
Should Java need gap coverage later, the natural form is a `CheckJavaPackageData`
that asks the database which of the eighteen packages have no row for the target
`week_start` — not a reshaping of this collector into eighteen requests.
