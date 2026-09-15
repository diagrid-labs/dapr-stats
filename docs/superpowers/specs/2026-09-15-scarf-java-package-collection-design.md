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
export endpoint and the same `SCARF_DAPR_API_TOKEN` that the three existing
Scarf collectors use.

## Goals

1. Collect JVM package downloads weekly and automatically, per version.
2. Cover every `io.dapr` artifact Scarf knows about, including ones registered
   later, without a code change.
3. Add no wall-clock time to the collection run.
4. Free a `workflow_dispatch` input slot, since `run-workflow.yaml` sits at
   GitHub's cap of 10.

## Non-goals

- **Backfilling 2025-04 to 2026-09.** The v3 window is capped at 366 days and
  Scarf's own history does not reach back to the gap. It stays a hole.
- **Migrating or rewriting `java_dapr_sdk`.** It is frozen as the Sonatype
  archive. Different cadence, different naming, different source.
- **Company attribution for packages.** `by-company` is available on this
  endpoint but is not collected here; the company leaderboard is a pixel
  concern and adding a second dimension multiplies the row count.
- **Gap detection and retry for this collector.** It makes one export request
  rather than a per-package loop, so the failure mode the
  [package collection gap handling design](2026-09-15-package-collection-gap-handling-design.md)
  addresses does not arise — that design's non-goals exclude Scarf for the same
  reason.

## What the API gives us

Everything below was verified against the live API on 2026-09-15 using
`scripts/scarf-probe.ps1`, not inferred from the OpenAPI document.

### The metric is an install event count

Scarf names the counter `total_installs` on `/v2/packages/{owner}/overview` and
`total` on a v3 export row. One event is one artifact fetch. Each row carries
three counters:

| Field | Meaning | Collected |
|---|---|---|
| `total` | install events — the download count | yes, as `downloads` |
| `unique_origins` | distinct origin IPs behind those events | yes |
| `unique_endpoints` | distinct Scarf routes hit | no — `1` on all 2,437 probe rows |

### The unit is (ISO week, package, version)

`rollup=weekly` returns Monday-anchored buckets and `end_date` is exclusive,
exactly as the pixel endpoints behave, so `IsoWeek` needs no adjustment.
`version` is populated on every row — 79 to 101 distinct versions per week for
`dapr-sdk`, which matches the ~77 versions per month the Sonatype CSVs carried.
`artifact_name` is the full coordinate (`io.dapr/dapr-sdk`) and `artifact` is
the package UUID.

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

### The magnitude is consistent with Maven Central

Three weeks of `io.dapr/dapr-sdk` sum to 82,093, and Scarf's own last-month
figure for it is 116,715 — a fourth week at ~34k accounts for the difference.
Against the last Sonatype import in `java_dapr_sdk` (39,926 for April 2025),
that is 2.9x over sixteen months.

This is consistent with the two sources measuring the same population. It is
not proof of it, and nothing in this design depends on the two being
interchangeable: `java_dapr_sdk` stays frozen precisely so nobody has to assume
they are.

### Eighteen JVM packages exist, not ten

`GET /v2/packages/Dapr/overview?type=repository-jvm&per_page=50`, last-month
installs:

| Package | UUID | Installs |
|---|---|---|
| `io.dapr/dapr-sdk` | `5213627a-3140-46d2-8544-3c149f948b22` | 116,715 |
| `io.dapr/dapr-sdk-autogen` | `1f253034-25e4-4100-908f-138324b0d5e1` | 109,242 |
| `io.dapr/dapr-sdk-parent` | `b6f8c52a-f0ca-428c-8f7e-c88225413166` | 66,333 |
| `io.dapr/dapr-sdk-actors` | `4073052e-8496-4198-b847-0237456a0c02` | 46,034 |
| `io.dapr/dapr-sdk-springboot` | `ad5b81cc-c045-495e-b138-1704ca0570a9` | 42,269 |
| `io.dapr/dapr-sdk-workflows` | `7bdae281-3117-42b4-b5f0-99567c9d6549` | 35,326 |
| `io.dapr/durabletask-client` | `464aca5a-e841-423b-b712-97051a4b5da3` | 32,601 |
| `io.dapr.spring/dapr-spring-messaging` | `693c9eed-65b7-41a8-9257-958f9140ee99` | 20,296 |
| `io.dapr.spring/dapr-spring-workflows` | `b5459416-2d72-4d29-b1d8-17386c47aa6c` | 19,105 |
| `io.dapr.spring/dapr-spring-parent` | `fd1f6921-6c8b-4583-8019-a9c352bcbae3` | 11,806 |
| `io.dapr.spring/dapr-spring-data` | `80250030-1b5d-495e-9845-a98e5c9ebf9b` | 4,681 |
| `io.dapr/testcontainers-dapr` | `585f1ca7-0b31-41ba-9ff3-9b27fe6360d7` | 4,525 |
| `io.dapr.spring/dapr-spring-boot-autoconfigure` | `0d1e80df-9a30-4b62-92e0-3814ce4bcffa` | 4,344 |
| `io.dapr.spring/dapr-spring-boot-starter` | `1b017f96-4425-42e0-bd22-9ab1cdae9b70` | 4,032 |
| `io.dapr.spring/dapr-spring-boot-tests` | `f009935d-6814-4bbd-a7bb-afb93f994967` | 2,930 |
| `io.dapr.spring/dapr-spring-boot-starter-test` | `20e11579-4927-4961-a641-cd18a4873f34` | 2,523 |
| `io.dapr/client` | `e4076618-562d-457f-8db5-7c6b7cda7af8` | 139 |
| `io.dapr/dapr-sdk-springboot3` | `2d867bd2-2b9a-4a8e-a525-04b8191e483e` | 112 |

The first ten are the originally requested list. All eighteen are collected:
`testcontainers-dapr` and the four `dapr-spring-boot-*` artifacts carry real
volume, and collecting by pattern rather than by list means a nineteenth
package is picked up the week it is registered.

### `package_id=all` is not JVM-only

A probe with `package_id=all` returned a `Dapr CLI` artifact alongside the
eighteen. The selector must name what it wants.

### Scarf returns one row per key already

Zero duplicate `(date, artifact_name, version)` keys across 2,437 probe rows.
Unlike `GetScarfBuildingBlockViews`, which re-sums because its referer
normalisation is many-to-one, this collector needs no aggregation step —
parse, then insert.

Longest observed `version` is 15 characters, longest `artifact_name` 45.
Volume is ~800 rows per week, so ~42,000 rows per year.

## Design

### Fetch

A new activity `GetScarfJavaDownloads : WorkflowActivity<ScarfJavaInput, bool>`,
in `CollectDaprStats/GetScarfJavaDownloads.cs`. It is a fourth caller of the
existing `ScarfExportClient` and declares its own `Owner = "Dapr"` and
`ApiTokenSecret = "SCARF_DAPR_API_TOKEN"`, following the per-activity
convention the two-account change established.

`ScarfExportRequest` gains one field:

```csharp
string? Query,   // package-name DSL; mutually exclusive with PixelIds
```

emitted by `BuildUrl` as `&query=`, URL-escaped. The DSL and the id selectors
cannot be combined — the v3 spec says the package-name query works with neither
`package_id` nor `tracking_pixel_id` — so `BuildUrl` throws `ArgumentException`
when both `Query` and a non-empty `PixelIds` are set, rather than letting Scarf
answer 422 at runtime.

The request is `rollup=weekly`, `breakdown_set=by-version`, `format=json`, and
`GroupByArtifact: null`. The null matters: the parameter defaults to `true`
server-side, which is what yields one row per artifact. Omitting it preserves
the deliberate asymmetry `ScarfExportClient` already documents.

Failures are caught and reported as `false`, never rethrown, for the reason the
other Scarf activities give: this runs under `Task.WhenAll` and an exception
would take down the unrelated GitHub collection below it.

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
`company_name` is correct for the three pixel collectors and wrong only for
this breakdown — a by-version row has no company, so reusing `Parse` here would
return zero rows from a healthy response. The private `ReadWeekStart`,
`ReadString` and `ReadCount` helpers are shared; `ParsePackageVersions` skips
rows missing `artifact_name` or `version` on the same defensive grounds.

### Store

```sql
CREATE TABLE scarf_java_downloads (
    id SERIAL PRIMARY KEY,
    week_start DATE NOT NULL,
    package_name VARCHAR(255) NOT NULL,
    package_version VARCHAR(100) NOT NULL,
    downloads BIGINT NOT NULL,
    unique_origins BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    UNIQUE (week_start, package_name, package_version)
);
```

Writes are delete-then-insert, scoped per `(week_start, package_name)` — per
package, not per week alone. This mirrors `GetScarfPageViews`, which scopes its
delete per week *and site* so that a site gone quiet across the whole window is
left untouched rather than erased. Same reasoning: a package Scarf returns
nothing for should keep the rows it has.

Delete-then-insert rather than upsert because Scarf's figures for a recent week
can still move, and a version that drops out of a week must not be left behind
as a phantom. The `UNIQUE` constraint is the second mechanism the
[collector week deduplication design](2026-09-08-collector-week-deduplication-design.md)
asks every source to carry, and it would catch a parser bug producing duplicate
keys before Postgres stored them.

Inserts are chunked at 500 rows per statement: 500 x 6 = 3,000 parameters,
well inside Postgres' 65,535 limit, and ~800 rows a week means two statements.

Every `InsertAsync` is guarded by `if (!input.SkipStorage)`, per convention.

**The whole-response zero-row guard is carried over from
`GetScarfBuildingBlockViews` and is load-bearing here.** If the export returns
nothing across every week — a revoked token, a renamed artifact, a query DSL
that stops matching — delete-then-insert would wipe real weeks and write
nothing back, and the oldest would fall outside the window before the next run
could repair it. The activity logs a warning naming those causes and returns
`false` without performing a single delete.

### Workflow wiring

`CollectorWorkflowInput` gains `string[] ScarfJavaPackages`. Entries are
package-name DSL patterns, joined with `,` (the DSL's top-level alternation) to
form the `query` value. The default is the single pattern `io.dapr*`, which
covers all eighteen and picks up future registrations; an explicit coordinate
such as `io.dapr/dapr-sdk` allows a surgical single-package re-run.

The call joins the existing `scarfTasks` block, guarded with `?.Length > 0` so
an empty list skips the source:

```csharp
if (input.ScarfJavaPackages?.Length > 0)
{
    scarfTasks.Add(context.CallActivityAsync(
        nameof(GetScarfJavaDownloads),
        new ScarfJavaInput(input.ScarfJavaPackages, input.SkipStorage)));
}
```

It is deliberately **not** wired into the `CollectNuGet/Npm/Python` retry loop
pattern. Those loops exist because a per-package fan-out against NuGet, npm and
PyPI provokes 429s; this is one export request covering eighteen packages and
three weeks. No `CheckJavaPackageData` activity, no five-minute backoff, and
**no increase to the ~25-minute run time** — so neither the job's 30-minute
timeout nor the 25-minute poll deadline needs raising.

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

Accepted values:

| Value | Meaning |
|---|---|
| `all` (or empty, as on a scheduled run) | every ecosystem at its `DEFAULT_*` list |
| `none` | no package collection at all |
| `java:all` | only the named ecosystems; **every unnamed one is skipped** |
| `nuget:Dapr.Client,Dapr.Jobs;java:all` | explicit per-package lists where wanted |

The "unnamed means skipped" rule is the conservative reading and is the point
of the syntax: a group string is written to re-run something specific, and
quietly collecting four other ecosystems alongside it is the failure that made
`none` a keyword in the first place. The `all`/`none` keywords survive at the
top level for the same reason they exist today — an emptied text input comes
back as its `default:`, so emptiness can never mean "skip".

`DEFAULT_NUGET_PACKAGES`, `DEFAULT_NPM_PACKAGES` and `DEFAULT_PYTHON_PACKAGES`
stay as they are and are joined by `DEFAULT_JAVA_PACKAGES: 'io.dapr*'`. The
parser in the `Generate Variables` step gains a `resolve_group` helper beside
the existing `resolve_list`, and the payload gains `ScarfJavaPackages`.

This leaves two free input slots.

### Tests

New xUnit cases, consistent with the suite's rule that only pure helpers are
covered:

- `ScarfAggregationResponseTests`: `ParsePackageVersions` over a fixture cut
  from the probe dump — row count, week bucketing, version strings, counters,
  and that a row missing `artifact_name` or `version` is skipped. The fixture
  carries package and version names only, so unlike the RUM fixtures it holds
  no PII and can be committed.
- A regression case asserting the existing `Parse` still drops null-company
  rows, so the two paths cannot be collapsed later by accident.
- `ScarfExportClient.BuildUrl`: `query=` is emitted and escaped,
  `group_by_artifact` is absent when `GroupByArtifact` is null, and `Query`
  together with a non-empty `PixelIds` throws.
- The CI input parser's group syntax, if it is extracted somewhere testable;
  otherwise its cases are documented in `local-tests.http`.

### Schema and docs

- `postgres/postgres_schema.psql` — add `scarf_java_downloads`.
- `postgres/add-scarf-java-downloads-2026-09-15.psql` — the dated migration,
  dry-run first inside an aborting `DO` block per the house rule, and applied
  only after explicit approval.
- `postgres/java-import.md` — a note that the manual ritual is superseded from
  2026-09 and that `java_dapr_sdk` is a frozen archive.
- `local-tests.http` — a `ScarfJavaPackages` payload.
- `README.md` — add the data source.
- `AGENTS.md` — record the `package_id=all` trap and that `Parse` has two
  entry points now.

## Open question: `WeeksPerRun`

The other Scarf collectors re-fetch three complete weeks, because Scarf's
company attribution is retroactive and the overlap lets a correction land.

Three weeks of probe data show every artifact declining uniformly —
`dapr-sdk` runs 34,178 then 27,502 then 20,413, and the same ~40% slide appears
on all eighteen JVM artifacts including the smallest. That is either a real
CI-driven spike in the 2026-08-24 week or Scarf back-filling recent weeks late.
The two have different consequences: if it is lag, three weeks may not be
enough overlap for a late correction to land, and the most recent week would be
persistently understated between runs.

A ten-week probe settles it, and doubles as a test of whether `query=io.dapr*`
selects exactly the JVM packages and excludes `Dapr CLI`:

```powershell
.\scripts\scarf-probe.ps1 "https://api.scarf.sh/v3/insights/Dapr/aggregations/export?start_date=2026-07-06&end_date=2026-09-14&query=io.dapr*&rollup=weekly&breakdown_set=by-version&format=json" -OutFile scratch\scarf-jvm-10weeks.json
```

`WeeksPerRun` is 3 unless that probe shows the tail weeks recovering, in which
case it rises to cover the lag. If the DSL does not behave, the fallback is
eighteen repeated `package_id` parameters and a `PackageIds` field on
`ScarfExportRequest` instead of `Query` — the rest of this design is unchanged
either way.

## Interaction with in-flight work

The [package collection gap handling design](2026-09-15-package-collection-gap-handling-design.md)
touches three of the same files. Neither blocks the other, but whoever lands
second should expect textual conflicts:

- `CollectorWorkflow.cs` — that design changes `CollectorWorkflow` to
  `Workflow<CollectorWorkflowInput, CollectorWorkflowResult>`; this one adds a
  call inside `RunAsync`.
- `CollectorWorkflowInput` — that design adds `bool GapFillOnly`, this one adds
  `string[] ScarfJavaPackages`. Both must also reach the JSON payload in
  `run-workflow.yaml`.
- `run-workflow.yaml` — both edit the `Generate Variables` step. The 10-to-8
  input merge here frees the slot a `gap_fill_only` input would otherwise have
  nowhere to go.

Scarf is outside that design's non-goals for gap handling, and this collector's
single-request shape is why.
