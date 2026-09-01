# Scarf pixel event weekly collection — design

Date: 2026-09-01

## Problem

Both `dapr.io` and `docs.dapr.io` carry a Scarf tracking pixel. Scarf resolves a
visitor's IP to a company, so it can answer a question no other data source in
this repo can: *which companies are reading which parts of the Dapr
documentation.*

That data is only reachable through the Scarf API or the Scarf web UI. Nothing
in `dapr-stats` collects it, so there is no long-term record and no way to chart
it in Grafana alongside package downloads, GitHub activity and Discord numbers.

Two specific questions are unanswerable today:

1. Which companies are looking at which **building block** — the pages under
   `https://docs.dapr.io/developing-applications/building-blocks/`. This is the
   closest available proxy for which Dapr APIs a company is evaluating.
2. Which companies visit `dapr.io` and `docs.dapr.io` at all, ranked by volume,
   week over week.

## Goal

Two new Postgres tables, populated as part of the existing `CollectorWorkflow`,
plus two views that shape them into the tables a human actually wants to read.

**`scarf_building_block_views`** — one row per ISO week per building block per
company:

- `views` — page views Scarf attributed to that company on that building block
- `unique_visitors` — distinct visitor origins, summed across pages (see
  [`unique_visitors` is not additive](#unique_visitors-is-not-additive))

**`scarf_company_views`** — one row per ISO week per company, across both sites
combined:

- `views`, `unique_visitors` — as above, but `unique_visitors` here is exact

Each run fetches the last three *complete* ISO weeks and rewrites them.

## Non-goals

- **Dwell time.** A Scarf pixel records a fetch, not a session. There is no
  duration to collect. "Companies spending time on the building block pages" is
  served by view counts and nothing better exists in the data.
- **Traffic Scarf could not attribute to a company.** The
  `by-referer,by-company` breakdown returns only company-identified rows (0 of
  ~4,100 rows in the sample week had a null company). Total per-page views
  regardless of company would need a third request with `breakdown=by-referer`.
  Not collected; add it when someone asks.
- **Backfill.** Scarf has data back to the docs pixel's creation on 2022-12-16,
  and the v3 export accepts windows up to 366 days. History could be seeded, but
  the collector is rolling-window only. History accumulates from the first run
  forward.
- **Pages outside `/building-blocks/`.** The concepts, getting-started,
  operations and reference sections are dropped. Only the building-block tree
  and its index page are stored.
- **Third-party mirrors, for `scarf_building_block_views` only.**
  `dapr.website.cncfstack.com` and `blue-dune-0da9d541e.7.azurestaticapps.net`
  serve the Dapr docs with Dapr's pixel embedded. They are not sites the Dapr
  project controls and `ScarfReferer`'s host allow-list excludes them from the
  building-block table. `scarf_company_views` gets no such filter: it keys off
  `tracking_pixel_id`, and `breakdown=by-company` returns no `referer` to
  filter on, so mirror traffic on the docs pixel counts toward the company
  leaderboard.

## Verified facts

Everything below was confirmed against the live API on 2026-09-01 using
`scripts/scarf-probe.ps1`, not inferred from the OpenAPI document.

**The API contract.** The rendered docs at `https://api-docs.scarf.sh/v2.html`
are Redoc over `https://api.scarf.sh/static/api-v2.yaml`. Base URL is
`https://api.scarf.sh`. Authentication is `Authorization: Bearer <token>`.

**The owner slug is `Dapr`, capitalised.** `/v2/*` endpoints accept `dapr`, but
`/v3/insights/dapr/...` returns `404 {"detail":"Organization not found"}`. Only
`Dapr` works on v3.

**Two pixels, both owned by `Dapr`:**

| Pixel | Id | Views, last month |
|---|---|---|
| `Dapr Docs` | `4848fb3b-3edb-4329-90a9-a9d79afff054` | 61,587 |
| `dapr.io` | `0f63416a-15c2-4ccd-bae0-001898f75f8f` | 499 |

The docs pixel is a plain `<img class="img-scarf">` and always fires. The
dapr.io pixel is injected by JavaScript only after cookie consent is accepted,
and the pixel was created 2026-08-19, so its numbers are both recent and
structurally low. This is a property of the sites, not something the collector
can fix.

**`GET /v3/insights/Dapr/aggregations/export` is available on Dapr's plan.**
The spec documents a `403 — "The organization's plan does not include
aggregation exports"`; it does not occur. Confirmed working with `format=json`,
`rollup=weekly`, `breakdown=by-company`, `breakdown_set=by-referer,by-company`,
repeated `tracking_pixel_id`, and `group_by_artifact=false`.

**`end_date` is exclusive and weeks are Monday-anchored.** A request for
`start_date=2026-08-24&end_date=2026-08-31` returned rows with exactly one
distinct `date`: `2026-08-24`, a Monday. This matches the existing `IsoWeek`
helper with no adjustment.

The v2 CSV endpoints behave differently — `/v2/companies/Dapr/pixel-rollup` with
the same dates returned rows dated both `2026-08-24` and `2026-08-31`, so its
`end_date` is inclusive. The design does not use v2, but the discrepancy is
recorded so nobody assumes the two are interchangeable.

**`group_by_artifact=false` merges pixels server-side.** With both
`tracking_pixel_id` values and `breakdown=by-company`, rows come back with
`artifact: ""` and one row per company across both sites. This is what makes the
combined leaderboard's `unique_origins` correct rather than a double-counted sum.

**Volume is small.** A full week of `by-referer,by-company` on the docs pixel is
~4,100 rows, of which 830 are building-block pages. No pagination parameters
exist on this endpoint and none are needed.

**Response shape.** Each row of the `data` array carries a large, mostly-null
field set. The fields this design reads:

```json
{
  "date": "2026-08-24",
  "rollup": "weekly",
  "referer": "https://docs.dapr.io/developing-applications/building-blocks/workflow/workflow-overview/",
  "company_name": "Apple",
  "company_domain": "apple.com",
  "total": 1,
  "unique_origins": 1
}
```

**`referer` carries the full page URL**, because both pixels set
`referrerPolicy="no-referrer-when-downgrade"` and both sites are HTTPS. It is
also messy, in four distinct ways, all present in the sample week:

| Shape | Example | Count |
|---|---|---|
| Versioned docs subdomain | `https://v1-17.docs.dapr.io/reference/environment/` | ~110 rows across `v1-9` … `v1-18` |
| Third-party mirror | `https://dapr.website.cncfstack.com/reference/cli/dapr-help/` | 14 rows |
| Query string | `https://docs.dapr.io/getting-started/install-dapr-cli/?ref=…`, `?utm_source=chatgpt.com` | 285 rows |
| Localised path | `https://docs.dapr.io/zh-hans/reference/` | present, uncounted |

**The 12 building blocks**, confirmed against the docs index page, with the
sample week's row counts:

| Segment | Rows |
|---|---|
| `workflow` | 262 |
| `pubsub` | 139 |
| *(index page, empty segment)* | 104 |
| `service-invocation` | 97 |
| `state-management` | 56 |
| `actors` | 45 |
| `jobs` | 42 |
| `bindings` | 24 |
| `secrets` | 23 |
| `configuration` | 14 |
| `conversation` | 14 |
| `distributed-lock` | 10 |
| `cryptography` | 0 |

## Architecture

Two activities, each making exactly one HTTP request per run. Both requests
cover all three weeks in a single window, because `rollup=weekly` returns one
bucket per week in one response.

Let `M0` be the Monday that starts the current, in-progress ISO week, and `M3`
the Monday three weeks before it. `IsoWeek.CompleteWeeksBefore(utcNow, 3)`
already produces these: `windows[0].From` is `M3` and `windows[^1].To` is `M0`.

**Request 1 — building blocks.**

```
GET https://api.scarf.sh/v3/insights/Dapr/aggregations/export
      ?start_date=<M3>
      &end_date=<M0>
      &tracking_pixel_id=4848fb3b-3edb-4329-90a9-a9d79afff054
      &rollup=weekly
      &breakdown_set=by-referer,by-company
      &format=json
```

**Request 2 — company leaderboard.**

```
GET https://api.scarf.sh/v3/insights/Dapr/aggregations/export
      ?start_date=<M3>
      &end_date=<M0>
      &tracking_pixel_id=4848fb3b-3edb-4329-90a9-a9d79afff054
      &tracking_pixel_id=0f63416a-15c2-4ccd-bae0-001898f75f8f
      &rollup=weekly
      &breakdown=by-company
      &group_by_artifact=false
      &format=json
```

`include_low_confidence` is left at its default of `true`. The raw event export
shows confidences like `0.874`; excluding those would shrink the data for a
precision gain nobody asked for.

Both requests build their `tracking_pixel_id` parameters by repeating one per
element of the activity's `PixelIds` array — the requests above show the arrays
this design ships with, not a fixed count. Request 1 leaves `group_by_artifact`
at its default of `true`; with more than one pixel that yields per-pixel rows,
which the re-aggregation step in
[`GetScarfBuildingBlockViews`](#getscarfbuildingblockviews-new-activity) folds
together anyway. Request 2 sets it to `false` deliberately, because that is what
makes `unique_origins` correct across the two sites.

### Why not the v2 endpoints

`/v2/tracking-pixels/{owner}/{pixel_id}/events` returns raw per-event CSV with
`referer`, `origin_company`, `origin_domain` and `origin_id`. It would work, but
it means parsing ~14,000 events per week and reimplementing an aggregation Scarf
already performs correctly.

`/v2/companies/Dapr/pixel-rollup` returns
`date,pixel,pixel_name,company,company_domain,total,total_unique` and is a close
fit for the leaderboard, but it is grouped *per pixel*. Summing `total_unique`
across the two pixels double-counts anyone who visited both sites.
`group_by_artifact=false` on v3 solves that server-side.

### Why three weeks

Matches the `datadog_rum` collector, but for a different reason. DataDog's
lookback is bounded by 30-day retention; Scarf retains years. Here the overlap
exists because **Scarf's company attribution is retroactive** — a visitor
identified days after the fact changes an earlier week's numbers. Re-fetching
and rewriting three weeks lets those corrections land.

Three weeks is also what makes a single failed run self-healing: the next run
re-collects everything the failed one would have written.

## Components

### `ScarfReferer` (new, static)

Pure string handling, no I/O, the fiddliest logic in this design and therefore
the most heavily unit-tested. One entry point:

```csharp
public static bool TryGetBuildingBlock(string? referer, out string buildingBlock)
```

The steps, in order:

1. Parse as an absolute URI. Anything unparseable returns `false`.
2. Host allow-list: `docs.dapr.io`, or a host matching `v<version>.docs.dapr.io`
   where `<version>` is the `v1-17` / `v1-13-1` shape. Everything else returns
   `false`, which is what drops the third-party mirrors.
3. Take `uri.AbsolutePath`. Query and fragment are discarded here, which
   collapses the `?utm_source=…` and `?ref=…` duplicates.
4. Find the segment `/building-blocks/` in the path. Absent, return `false`.
   Language prefixes need no special handling:
   `/zh-hans/developing-applications/building-blocks/pubsub/…` and
   `/developing-applications/building-blocks/pubsub/…` both yield `pubsub`.
5. Take the first path segment after `/building-blocks/`, lowercased.
6. An empty segment — the index page, `…/building-blocks/` — yields the literal
   `(index)`. Parenthesised so it can never collide with a real URL segment.

The returned segment is **not** validated against the list of 12. A building
block added to the docs appears in the table with no code change; only the pivot
view needs a new line.

### `ScarfAggregationResponse` (new, static)

Parses the `{"data":[...]}` envelope into:

```csharp
public sealed record Row(
    DateOnly WeekStart,
    string? Referer,
    string CompanyName,
    string CompanyDomain,
    long Total,
    long UniqueOrigins);
```

`date` is parsed with `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)`,
never `DateTime.Parse`, for the same reason `PostgresOutput.ParseDate` avoids
it: a bare date through a culture-sensitive parse can shift the calendar day.

`company_domain` is nullable in the API's own schema. Every row in the sample
week had one, but a null is coalesced to `""` rather than trusted, because the
column is part of the primary key.

Rows whose `company_name` is null or empty are dropped. None were observed;
dropping them keeps a null out of a `NOT NULL` key column if that ever changes.

### `SqlValuesBuilder` (new, static)

Builds the `($1,$2,$3),($4,$5,$6),…` clause for a chunked multi-row insert:

```csharp
public static string Build(int rowCount, int columnsPerRow)
```

Small, but parameter numbering is exactly the kind of arithmetic that is wrong
by one and silently writes the wrong column, so it is separated out and tested
rather than inlined twice.

### `GetScarfBuildingBlockViews` (new activity)

`WorkflowActivity<ScarfInput, bool>` where
`ScarfInput(string[] PixelIds, bool SkipStorage)`. The record is declared once,
at the bottom of this file, the way `DataDogRumInput` is declared at the bottom
of `GetDataDogRumData.cs`; `GetScarfCompanyViews` reuses it.

Passing a non-docs pixel here is harmless rather than wrong: the host allow-list
in `ScarfReferer` discards everything that is not a Dapr docs host, and dapr.io
has no building-block pages to contribute.

1. Read `SCARF_DAPR_API_TOKEN` from the `secretstore`.
2. Compute the window from `IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, 3)`.
3. Issue request 1. A non-2xx throws with the status code and body, as
   `GetDataDogRumData` does.
4. Map each row through `ScarfReferer.TryGetBuildingBlock`, discarding rows that
   return `false`.
5. **Re-aggregate.** Normalisation is many-to-one — `?utm_source=…` variants,
   `v1-17.docs.dapr.io`, `/zh-hans/…` and every page beneath a building block
   all collapse to one key. Group the surviving rows by
   `(WeekStart, buildingBlock, CompanyName, CompanyDomain)` and sum `Total` and
   `UniqueOrigins`. Skipping this step would produce duplicate primary keys.
6. Group by week and write each week independently (see
   [Write strategy](#write-strategy)). `SkipStorage` logs the per-week row count
   and writes nothing.

Returns `true` only if every week was written.

### `GetScarfCompanyViews` (new activity)

Same shape, request 2, and simpler: no referer handling and no re-aggregation,
because `breakdown=by-company` already returns one row per `(week, company)`.
Group by week, write per week.

### `Program.cs`

No change. Both activities take `IHttpClientFactory`, `PostgresOutput` and
`DaprClient`, all already registered, and `AddDaprWorkflow()` discovers
activities by convention.

## Database schema

Added to `postgres/postgres_schema.psql`, matching the house style every other
table in that file uses: a surrogate `SERIAL PRIMARY KEY`, a named `UNIQUE`
constraint carrying the real key, `VARCHAR(255)` rather than `text`, `TIMESTAMP`
rather than `timestamptz`, and no `IF NOT EXISTS` — the file is applied once,
by hand, not run idempotently.

### `scarf_building_block_views`

```sql
CREATE TABLE scarf_building_block_views (
    id SERIAL PRIMARY KEY,
    week_start DATE NOT NULL,
    building_block VARCHAR(255) NOT NULL,
    company_name VARCHAR(255) NOT NULL,
    company_domain VARCHAR(255) NOT NULL DEFAULT '',
    views BIGINT NOT NULL,
    unique_visitors BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT scarf_building_block_views_unique
        UNIQUE (week_start, building_block, company_name, company_domain)
);
```

### `scarf_company_views`

```sql
CREATE TABLE scarf_company_views (
    id SERIAL PRIMARY KEY,
    week_start DATE NOT NULL,
    company_name VARCHAR(255) NOT NULL,
    company_domain VARCHAR(255) NOT NULL DEFAULT '',
    views BIGINT NOT NULL,
    unique_visitors BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT scarf_company_views_unique
        UNIQUE (week_start, company_name, company_domain)
);
```

`company_domain` is `NOT NULL DEFAULT ''` with nulls coalesced at insert.
`company_name` alone is not a safe key — two companies can share a display
name — and the domain alone is not safe either while it is nullable in the API
schema. Both, together, are what the named `UNIQUE` constraint enforces; the
surrogate `id` stays the `PRIMARY KEY` so the tables follow the same shape as
every other table in the schema, rather than the composite `PRIMARY KEY` an
earlier draft of this design used.

### `scarf_building_block_company_view`

The table the whole exercise is for: one column per building block, one row per
company per week.

```sql
CREATE OR REPLACE VIEW scarf_building_block_company_view AS
SELECT
    week_start,
    company_name,
    company_domain,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'actors'), 0)             AS actors,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'bindings'), 0)           AS bindings,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'configuration'), 0)      AS configuration,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'conversation'), 0)       AS conversation,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'cryptography'), 0)       AS cryptography,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'distributed-lock'), 0)   AS distributed_lock,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'jobs'), 0)               AS jobs,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'pubsub'), 0)             AS pubsub,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'secrets'), 0)            AS secrets,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'service-invocation'), 0) AS service_invocation,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'state-management'), 0)   AS state_management,
    COALESCE(SUM(views) FILTER (WHERE building_block = 'workflow'), 0)           AS workflow,
    COALESCE(SUM(views) FILTER (WHERE building_block = '(index)'), 0)            AS index_page,
    SUM(views)                                                                   AS total
FROM scarf_building_block_views
GROUP BY week_start, company_name, company_domain;
```

A building block missing from the view still contributes to `total`, so a
newly-added block is visible as a gap between `total` and the sum of the columns
until the view is extended.

### `scarf_top_companies_view`

```sql
CREATE OR REPLACE VIEW scarf_top_companies_view AS
SELECT week_start, company_name, company_domain, views, unique_visitors, rank
FROM (
    SELECT
        week_start,
        company_name,
        company_domain,
        views,
        unique_visitors,
        ROW_NUMBER() OVER (
            PARTITION BY week_start
            ORDER BY views DESC, company_name
        ) AS rank
    FROM scarf_company_views
) ranked
WHERE rank <= 100;
```

`ROW_NUMBER` rather than `RANK`, so a tie cannot return more than 100 rows.
`company_name` is the tiebreaker, making the ordering deterministic across runs.

The table stores every company; the cut to 100 lives only here and can be
raised, lowered or removed without touching stored data.

### `unique_visitors` is not additive

In `scarf_company_views` the value is exact: `group_by_artifact=false` makes
Scarf compute distinct origins across both pixels server-side.

In `scarf_building_block_views` it is **an upper bound**. Scarf returns
`unique_origins` per `(referer, company)` pair; normalisation collapses many
referers into one building block, and the collector sums. One person reading
three pub/sub pages counts as three. Summing the column across building blocks
compounds the same error again.

Treat `views` as the real metric and `unique_visitors` as a rough ceiling. The
pivot view deliberately exposes only `views` for this reason.

## Write strategy

Per week, in this order:

1. `DELETE FROM <table> WHERE week_start = $1`
2. Chunked multi-row `INSERT`, 500 rows per statement

Delete-then-insert rather than `ON CONFLICT DO UPDATE`, because the row *set* for
a week can shrink between runs. Scarf re-attributing a visitor removes a
`(page, company)` pair, and an upsert would leave the old pair in the table as a
phantom forever. Deleting the week first makes each collection authoritative.

The cost is that the Dapr Postgres binding exposes no transaction:
`PostgresOutput.InsertAsync` issues one statement per binding invocation. A
failure between the delete and the final insert leaves that week short until the
next run's three-week overlap repairs it. This is accepted rather than solved —
the alternative is transaction support in `PostgresOutput`, which is a larger
change than this feature justifies for a weekly analytics table with a single
writer and no live readers.

500 rows per statement keeps parameter counts far inside Postgres' 65,535 limit:
830 building-block rows at 7 columns is two statements and at most 3,500
parameters. `PostgresOutput.InsertAsync` already accepts arbitrary SQL and a
parameter array, so it needs no change.

Each week is wrapped in its own `try`/`catch`. One week failing must not discard
the other two — the same structure `GetDataDogRumData` uses.

## Workflow changes

`CollectorWorkflowInput` gains two fields, following the `DataDogRumServices`
convention where an empty array means "skip this source":

```csharp
public record CollectorWorkflowInput(
    DateTime CollectionDate,
    string[] NuGetPackageNames,
    string[] NpmPackageNames,
    string[] PythonPackageNames,
    string[] DockerHubImages,
    bool CollectDiscordData,
    bool CollectGitHubData,
    bool CollectDiagridDashboardData,
    string[] DataDogRumServices,
    string[] ScarfBuildingBlockPixelIds,
    string[] ScarfLeaderboardPixelIds,
    bool SkipStorage);
```

Two separate arrays because the two requests genuinely differ in scope: the
building-block breakdown only makes sense for the docs pixel, while the
leaderboard should span every pixel Dapr owns. Collapsing them into one array
would require the code to know which pixel is "the docs one", which is exactly
the coupling the workflow input exists to avoid.

In `CollectorWorkflow.RunAsync`, alongside the DataDog block:

```csharp
var scarfTasks = new List<Task>();

if (input.ScarfBuildingBlockPixelIds?.Length > 0)
{
    scarfTasks.Add(context.CallActivityAsync(
        nameof(GetScarfBuildingBlockViews),
        new ScarfInput(input.ScarfBuildingBlockPixelIds, input.SkipStorage)));
}

if (input.ScarfLeaderboardPixelIds?.Length > 0)
{
    scarfTasks.Add(context.CallActivityAsync(
        nameof(GetScarfCompanyViews),
        new ScarfInput(input.ScarfLeaderboardPixelIds, input.SkipStorage)));
}

if (scarfTasks.Count > 0)
{
    await Task.WhenAll(scarfTasks);
}
```

The owner slug `Dapr` is a `const` in the activities, not workflow input. It is a
property of the account the token belongs to, not a per-run choice.

## Secrets and CI changes

The secret store is `secretstores.local.env`, so the secret key is the
environment variable name.

- `CollectDaprStats/secrets.json.example` gains `"ScarfDaprApiToken": ""`
- `README.md` gains `export SCARF_DAPR_API_TOKEN=<SCARF_API_TOKEN_VALUE>` to the
  environment variable list, and Scarf to the data-source list
- `.github/workflows/run-workflow.yaml` gains
  `SCARF_DAPR_API_TOKEN: ${{ secrets.SCARF_DAPR_API_TOKEN }}` to `env:`, and both pixel arrays
  to the workflow start payload:

  ```json
  "ScarfBuildingBlockPixelIds": ["4848fb3b-3edb-4329-90a9-a9d79afff054"],
  "ScarfLeaderboardPixelIds": [
      "4848fb3b-3edb-4329-90a9-a9d79afff054",
      "0f63416a-15c2-4ccd-bae0-001898f75f8f"
  ]
  ```

A repository secret named `SCARF_DAPR_API_TOKEN` must be created before the first
scheduled run. Until it exists the activities throw and the workflow reports
failure.

**No schedule change.** `run-workflow.yaml` already runs at `0 9 * * 1` — 10:00
CET every Monday — which is what a weekly collector needs.

`scripts/scarf-probe.ps1`, written while designing this, stays in the repo as
the tool for interrogating the Scarf API by hand.

## Error handling

| Failure | Behaviour |
|---|---|
| `SCARF_DAPR_API_TOKEN` missing from the secret store | `GetSecretAsync` throws; the activity fails and Dapr retries it |
| Non-2xx from Scarf | `HttpRequestException` with status code and response body, matching `GetDataDogRumData` |
| Malformed JSON | `JsonException` propagates; the activity fails |
| A referer that will not parse | Row dropped silently. Bots and mangled referers are expected and are not an error |
| One week's write fails | Logged; the other weeks still commit; the activity returns `false` |
| One activity fails | The other still runs — they are separate `CallActivityAsync` calls under `Task.WhenAll` |

Both activities log a per-week summary line, as the DataDog activities do:

```
Scarf building blocks week 2026-08-24: 830 rows, 12 building blocks, 604 companies
Scarf companies week 2026-08-24: 1412 companies, 3908 views
```

## Verification

**Unit tests** (`CollectDaprStats.Tests`, xUnit, matching the existing files):

- `ScarfRefererTests` — `docs.dapr.io` accepted; `v1-17.docs.dapr.io` and
  `v1-13-1.docs.dapr.io` accepted; `dapr.website.cncfstack.com` and
  `blue-dune-0da9d541e.7.azurestaticapps.net` rejected; query string and
  fragment stripped; `/zh-hans/…` yields the same key as the English path; a deep
  page (`…/building-blocks/pubsub/howto-publish-subscribe/`) yields `pubsub`;
  `…/building-blocks/` yields `(index)`; a non-building-block page rejected;
  `null`, empty and unparseable input rejected.
- `ScarfAggregationResponseTests` — a realistic envelope parses; null
  `company_domain` becomes `""`; a null `company_name` row is dropped; `date`
  parses to the right `DateOnly` regardless of host time zone; an empty `data`
  array yields an empty result.
- `SqlValuesBuilderTests` — one row, several rows, correct parameter numbering
  across a chunk boundary.

**Manual**, via a new `local-tests.http` entry that sets every other source
empty:

1. `SkipStorage: true` first. Confirm the log lines report three weeks with
   plausible counts and nothing is written.
2. `SkipStorage: false`. Then confirm, through the binding:
   - `scarf_building_block_views` holds three distinct `week_start` values, all
     Mondays
   - the building blocks present are a subset of the known 12 plus `(index)`
   - `scarf_top_companies_view` returns exactly 100 rows for a populated week
     with `rank` running 1..100
   - `scarf_building_block_company_view` returns one row per company per week and
     its columns sum to `total`
3. Run it a second time. Row counts must not grow — this is what proves
   delete-then-insert works and the three-week overlap is idempotent.
4. Spot-check one company's `workflow` figure against the Scarf web UI.

## Known limitations

- **dapr.io is consent-gated.** Its pixel fires only after a visitor accepts
  cookies, so the leaderboard under-represents it relative to docs.dapr.io. The
  pixel was also created on 2026-08-19, so it has almost no history.
- **Company attribution is IP-based and imperfect.** The raw event export shows
  `UNKNOWN` companies and confidence scores below 1.0. A company appearing in the
  table means Scarf resolved an IP to it, not that an employee was definitely
  reading the page. VPN and cloud-provider traffic is systematically
  misattributed.
- **Only attributed traffic is stored.** Numbers in these tables are much lower
  than total page views and must not be presented as traffic figures.
- **`unique_visitors` on building blocks is an upper bound.** See
  [above](#unique_visitors-is-not-additive).
- **Views are not intent.** A view of the workflow page is evidence of a look,
  not of adoption. The tables answer "who looked at what", and nothing stronger.
- **The pivot view needs a line per new building block.** The table does not, and
  `total` reveals the omission.
