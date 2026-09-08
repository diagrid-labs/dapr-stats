# Scarf Diagrid page view weekly collection — design

Date: 2026-09-07

## Problem

`dapr-stats` collects Scarf pixel data for the **Dapr** Scarf account only:
which companies read which Dapr building block, and which companies visit
`dapr.io` and `docs.dapr.io` at all. There is a second Scarf account, **Diagrid**,
with a pixel on `diagrid.io` and a second pixel on `docs.diagrid.io`. Nothing
collects it, so there is no long-term record of which companies are reading
Diagrid's own website and product documentation, and no way to chart it beside
everything else in the database.

The Dapr collectors cannot be pointed at it as they stand. Three things are
hardcoded to a single account:

- The owner slug lives in an `ExportUrl` const in both
  `GetScarfBuildingBlockViews.cs` and `GetScarfCompanyViews.cs`.
- The secret key is a compile-time `const ApiTokenSecret`.
- Neither existing table has an account column, so two accounts' rows would
  merge under the existing unique constraints and each account's per-week
  `DELETE` would wipe the other's data.

## Goal

One new Postgres table and one new view, populated by one new activity inside
the existing `CollectorWorkflow`.

**`scarf_diagrid_page_views`** — one row per ISO week per site per page per
company:

- `views` — page views Scarf attributed to that company on that page
- `unique_visitors` — distinct visitor origins for that page (see
  [`unique_visitors` is not additive](#unique_visitors-is-not-additive))

**`scarf_diagrid_company_pages_view`** — one row per ISO week per company,
answering the question that motivated this: *how many pages did this company
view this week*, meaning total page views, with the website and docs split out.

Each run fetches the last three *complete* ISO weeks and rewrites them, matching
the existing Scarf collectors exactly.

Alongside this, the Scarf HTTP call is extracted into a shared
`ScarfExportClient` and all three activities move onto it, so the owner slug and
secret name become per-activity values rather than a single-account constant.

## Non-goals

- **A Diagrid company leaderboard table.** Company weekly totals are derived in
  the view as `SUM(views)` over the page rows. The one thing this loses is an
  exact per-company `unique_visitors`, which only Scarf's `by-company` breakdown
  can give. Not collected. Add a second activity when someone asks for it; that
  work is described under [Deferred](#deferred).
- **An account column on the existing Dapr tables.** Diagrid rows land in a
  separate table, so the two accounts never share a row. The migration remains
  pending for the day a second account writes to `scarf_company_views` or
  `scarf_building_block_views`.
- **Distinct-page counts.** `total_views` is the agreed metric. `COUNT(*)` per
  company would be free but is not part of the view; add it if the question
  changes.
- **Backfill.** Rolling window only, like the Dapr collectors. History
  accumulates from the first run forward. Scarf's v3 export accepts windows up
  to 366 days if seeding is ever wanted.
- **Traffic Scarf could not attribute to a company.** The
  `by-referer,by-company` breakdown returns only company-identified rows.
- **Hosts other than `diagrid.io` and `docs.diagrid.io`.** Third-party mirrors,
  staging hosts and localhost are dropped. See
  [Host allow-list](#host-allow-list).
- **A new GitHub Actions dispatch input.** `run-workflow.yaml` is at the
  10-input cap. Diagrid collection rides the existing `collect_scarf` checkbox.

## Facts this design rests on

Verified against the live API on 2026-09-01 for the Dapr account; the Diagrid
account uses the same endpoints.

- The v3 owner slug is **case-sensitive**. `https://api.scarf.sh/v3/insights/Dapr/…`
  works and `dapr` returns `404 {"detail":"Organization not found"}`. The
  Diagrid slug is `Diagrid`.
- `end_date` on the export endpoint is **exclusive**.
- `rollup=weekly` returns one bucket per ISO week, so one request covers all
  three weeks.
- The comma in `breakdown_set=by-referer,by-company` is sent **unescaped**.
- Scarf's company attribution is **retroactive** — a visitor identified days
  later changes an earlier week. This is why three weeks are rewritten per run
  rather than one.
- Scarf answers an unrecognised `tracking_pixel_id` with **HTTP 200 and
  `{"data":[]}`**, not an error.

### Pixel IDs

| Site | Pixel ID |
|---|---|
| `diagrid.io` | `0a39aa4e-64f2-4851-8a94-2c3417c264ae` |
| `docs.diagrid.io` | `030637aa-5521-4cbd-b1b7-3f9f97865a0e` |

One pixel per site, unlike Dapr's building-block collector which uses a single
pixel. This matters: because each pixel serves a distinct host, the
`(week, referer, company)` keys the two pixels produce are naturally disjoint,
so `group_by_artifact=false` is **not** needed here and the re-summing in
`Aggregate` is purely defensive. `site` is derived from the referer host, never
from which pixel a row came from — the `by-referer` breakdown does not carry the
pixel id.

## Design

### Shared Scarf client

New `CollectDaprStats/ScarfExportClient.cs`, registered in `Program.cs` and
constructor-injected into all three Scarf activities.

```csharp
public sealed record ScarfExportRequest(
    string Owner,            // "Dapr" | "Diagrid" — case-sensitive
    string ApiTokenSecret,   // "SCARF_DAPR_API_TOKEN" | "SCARF_DIAGRID_API_TOKEN"
    string[] PixelIds,
    DateOnly From,
    DateOnly To,
    string? Breakdown,       // "by-company", or null
    string? BreakdownSet,    // "by-referer,by-company", or null
    bool? GroupByArtifact);   // null = omit the parameter entirely

public sealed class ScarfExportClient
{
    internal static string BuildUrl(ScarfExportRequest request);
    public Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
        ScarfExportRequest request);
}
```

`BuildUrl` is a pure static so the URL shape — the part of this refactor that
can silently break the two working Dapr collectors — is unit-testable without a
network call. It must reproduce today's URLs byte for byte:

- `https://api.scarf.sh/v3/insights/{Owner}/aggregations/export`
- `start_date` / `end_date` as `yyyy-MM-dd`, invariant culture
- one `tracking_pixel_id` parameter per pixel, `Uri.EscapeDataString`d
- `rollup=weekly`
- `breakdown=…` or `breakdown_set=…` when non-null, the latter with its comma
  left unescaped
- `group_by_artifact` emitted only when `GroupByArtifact` is non-null, with that
  value; omitted entirely when it is null — see below
- `format=json`

`FetchAsync` resolves the token through `DaprClient.GetSecretAsync("secretstore",
request.ApiTokenSecret)`, sends the request with an
`Authorization: Bearer` header, throws `HttpRequestException` carrying the
status code and response body on non-2xx, and returns
`ScarfAggregationResponse.Parse(payload)`. `ScarfAggregationResponse` and
`ScarfReferer` are unchanged.

**The `GroupByArtifact` flag is not symmetric and must not be "tidied".**
`GetScarfCompanyViews` sends `group_by_artifact=false` because that is what
merges its two pixels server-side; without it `unique_origins` double-counts
anyone who visited both `dapr.io` and `docs.dapr.io`, so it passes
`GroupByArtifact = false`. `GetScarfBuildingBlockViews` does not
send the parameter at all, and neither does the new page collector, so both pass
`null`. This is why the field is `bool?`: omitting the parameter and sending
`group_by_artifact=true` are different requests, and a plain `bool` could not
tell them apart.

### Migrating the two Dapr activities

Both lose their private `FetchAsync`, `Iso`, `ExportUrl`, `ApiTokenSecret`,
`_httpClient` and `_daprClient` members, and take `ScarfExportClient` in the
constructor instead. `Owner = "Dapr"` and
`ApiTokenSecret = "SCARF_DAPR_API_TOKEN"` move to the call site as consts on
each activity class. Nothing else about them changes: the three-week window, the
zero-row guard, the delete-then-insert write path, the per-week logging and the
`bool` return all stay exactly as they are.

### New activity

`CollectDaprStats/GetScarfPageViews.cs` — a
`WorkflowActivity<ScarfInput, bool>`, reusing the existing
`ScarfInput(string[] PixelIds, bool SkipStorage)` record rather than adding a
near-identical one.

Class constants:

```csharp
private const int    WeeksPerRun     = 3;
private const string Owner           = "Diagrid";
private const string ApiTokenSecret  = "SCARF_DIAGRID_API_TOKEN";
private const string TableName       = "scarf_diagrid_page_views";
private const int    RowsPerStatement = 1000;   // 1000 x 8 = 8,000 params
```

`RowsPerStatement` is 1000 rather than the Dapr collectors' 500 because this
table is an order of magnitude wider — see [Volume](#volume). 8,000 parameters
is far inside Postgres' 65,535 limit.

Flow, mirroring `GetScarfBuildingBlockViews`:

1. `IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, 3)` gives the window.
2. One `ScarfExportClient.FetchAsync` with
   `BreakdownSet = "by-referer,by-company"`. On exception: log and `return false`
   without rethrowing — this activity runs under `Task.WhenAll` beside the two
   Dapr Scarf activities, and an unhandled exception would fail the whole
   workflow and skip the GitHub collection that follows it.
3. `Aggregate` maps each row's referer through `ScarfPage.TryGetPage`, drops
   rows that do not resolve, and re-sums by
   `(week, site, path, company_name, company_domain)`.
4. **Per-site zero-row guard.** `MissingSites` reports which of `ScarfPage.Sites`
   produced no rows in any of the three weeks. Zero rows for a site is
   indistinguishable between a genuinely quiet three weeks for that site, a
   wrong or revoked pixel id answered with HTTP 200 and `{"data":[]}`, and a
   host rename that stops `ScarfPage` matching anything for that site — and
   because the two pixels are answered independently, one going bad does not
   stop the other's rows from coming back. Two cases:
   - **Every site missing.** Nothing healthy to write. Log a warning naming
     the likely causes and `return false` *before the first `DELETE`*, exactly
     as the old all-sites guard did.
   - **Some but not all sites missing.** Log a warning naming the missing
     site(s) and the healthy ones continuing. The missing site's stored rows
     are never deleted — its pixel is presumed broken or revoked, so deleting
     three real weeks and writing nothing back would lose data the next run
     cannot repair, since the oldest of the three weeks falls outside its
     window. The healthy sites still store normally, including a per-week
     `DELETE` for a healthy site's own genuinely quiet week. `allSucceeded`
     starts `false` in this case, so a degraded run is reported as a failure.
5. Per week: log the row, page and company counts; skip storage entirely when
   `input.SkipStorage`; otherwise `StoreAsync`. A week Scarf returned nothing
   for is still written, because a genuinely empty week has to clear the
   previous collection's rows rather than leave them standing.
6. Return whether every week stored successfully.

### `StoreAsync`

Delete-then-insert per ISO week, for the same reason as the existing tables: a
page Scarf re-attributes to a different company must not linger, and an upsert
would leave the old `(page, company)` pair behind as a phantom forever. Unlike
the existing tables, the `DELETE` is also scoped **per site**, and `StoreAsync`
takes the caller's list of healthy sites (from step 4's guard) and loops over
it:

```sql
delete from scarf_diagrid_page_views where week_start = $1::date and site = $2
```

then, for that site's rows only, chunked inserts of
`(week_start, site, page_path, company_name, company_domain, views,
unique_visitors, collection_date)` built with
`SqlValuesBuilder.Build(chunk.Length, ColumnCasts)` where `ColumnCasts` is
`["date", null, null, null, null, null, null, null]`. `collectionDate` is
still computed once per `StoreAsync` call, shared across every site's inserts
in that call.

Scoping the delete by site is what makes per-site degradation in step 4
possible: a site left out of the `sites` list because its pixel has gone quiet
across the whole window never has its `DELETE` run at all, so its stored rows
are never touched, while a healthy site still gets its normal delete-then-
insert for every week — including a week that site genuinely had zero rows,
which still needs its `DELETE` to clear stale rows even though nothing is
inserted after it. This relies on `scarf_diagrid_page_views_unique` leading
with `(week_start, site, …)`, so no new index is needed to serve it.

The Dapr binding has no transaction, so a failure between a site's `DELETE` and
its last `INSERT` leaves that site's week short until the next run's
three-week overlap repairs it. This is the same exposure the existing Scarf
tables carry and is accepted for the same reason.

### Referer normalisation

New `CollectDaprStats/ScarfPage.cs`:

```csharp
public static bool TryGetPage(string? referer, out string site, out string path);
```

#### Host allow-list

The host must be `diagrid.io`, `www.diagrid.io` or `docs.diagrid.io`,
case-insensitively. `www.` is stripped so `site` becomes `diagrid.io`. Every
other host is rejected. This is what keeps out third-party mirrors, staging and
preview hosts, `localhost`, and any Dapr traffic that would appear if a pixel
were ever shared between the accounts. There are no versioned docs subdomains to
handle, unlike `docs.dapr.io`.

#### Path normalisation

From `uri.AbsolutePath`, which excludes the query and the fragment — that is
what collapses the `?utm_source=` and `?ref=` variants onto one key. Then:

1. `Uri.UnescapeDataString`.
2. Lowercase, invariant. Prevents `/Pricing` and `/pricing` splitting into two
   rows.
3. Collapse repeated slashes.
4. Ensure a leading `/`.
5. Strip the trailing `/`, except for the root, which stays `/`.
6. **Reject** if the result contains a control character, or exceeds 512
   characters.

Rejecting rather than truncating on over-length matches `ScarfReferer`'s guard
and exists for the same reason: an over-long value would fail its entire insert
chunk with Postgres error 22001 *after* the week's `DELETE` had already run.
Real paths on both sites are well under 200 characters.

Unescaping happens before validation, so the control-character and length
checks run on the text that will actually be stored. Unlike `ScarfReferer`,
there is no key extraction here for a percent-encoded `%2F` to smuggle a
delimiter past: the whole path is the key, so `/a%2Fb` simply unescapes to
`/a/b` and is stored. That collapses two technically distinct URLs onto one
row, which is harmless and vanishingly rare.

No language-prefix handling: `docs.diagrid.io` is not localised, so there is
nothing to collapse.

Asset paths are not filtered. A Scarf pixel fires on page load and the referer is
the page URL, not an asset URL, so there is nothing to exclude.

### Schema

```sql
CREATE TABLE scarf_diagrid_page_views (
    id SERIAL PRIMARY KEY,
    week_start DATE NOT NULL,
    site VARCHAR(255) NOT NULL,
    page_path VARCHAR(512) NOT NULL,
    company_name VARCHAR(255) NOT NULL,
    company_domain VARCHAR(255) NOT NULL DEFAULT '',
    views BIGINT NOT NULL,
    unique_visitors BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT scarf_diagrid_page_views_unique
        UNIQUE (week_start, site, page_path, company_name, company_domain)
);

CREATE INDEX scarf_diagrid_page_views_week_company
    ON scarf_diagrid_page_views (week_start, company_name, company_domain);
```

The unique constraint's own index leads with `week_start` but places `site` and
`page_path` ahead of the company columns, so it does not serve the view's
`GROUP BY`. Hence the second index. The constraint's worst-case index row is
about 1,280 bytes, inside btree's 2,704-byte limit.

`page_path` is `VARCHAR(512)`; every other width matches the existing Scarf
tables.

### View

```sql
CREATE OR REPLACE VIEW scarf_diagrid_company_pages_view AS
SELECT
    week_start,
    company_name,
    company_domain,
    SUM(views)                                                      AS total_views,
    COALESCE(SUM(views) FILTER (WHERE site = 'diagrid.io'), 0)      AS website_views,
    COALESCE(SUM(views) FILTER (WHERE site = 'docs.diagrid.io'), 0) AS docs_views
FROM scarf_diagrid_page_views
GROUP BY week_start, company_name, company_domain;
```

`total_views` is the requested metric: the total number of page views a company
made across both sites in that week. The website/docs split costs nothing given
the `site` column exists and answers the obvious follow-up question.

#### `unique_visitors` is not additive

`unique_visitors` is stored per page and summed across the two pixels inside
`Aggregate`, exactly as `GetScarfBuildingBlockViews` does. Summing distinct
counts across *pages* is never correct — one person reading five pages counts
five times — so no view exposes it. It is retained in the table because it is
meaningful per row and cannot be recovered later.

### Volume

Both sites together are on the order of a few hundred distinct pages, against
several hundred companies a week. Expect roughly 5,000–20,000 rows per week
against the Dapr building-block table's ~830, so up to about 1M rows in a year.
That is unremarkable for Postgres, and it is why `RowsPerStatement` is raised
and the second index exists. At 1,000 rows per statement a busy week is ~20
statements, ~60 per run.

### Wiring

`CollectorWorkflowInput` gains `string[] ScarfDiagridPagePixelIds`, guarded with
`?.Length > 0` and added to the existing `scarfTasks` list so it runs under the
same `Task.WhenAll` as the two Dapr Scarf activities.

`run-workflow.yaml`:

- `SCARF_DIAGRID_API_TOKEN: ${{ secrets.SCARF_DIAGRID_API_TOKEN }}` in `env:`
- `FIXED_SCARF_DIAGRID_PAGE_PIXEL_IDS: '0a39aa4e-64f2-4851-8a94-2c3417c264ae,030637aa-5521-4cbd-b1b7-3f9f97865a0e'`
- blanked out alongside the other two Scarf lists when `collect_scarf` is false
- `ScarfDiagridPagePixelIds` added to the jq payload

No new dispatch input: the file is at GitHub's 10-input cap, and Diagrid
collection belongs behind the existing `collect_scarf` checkbox.

Also updated: `dapr.yaml` (forward `SCARF_DIAGRID_API_TOKEN`), `local-tests.http`
(every payload plus the Scarf-only dry-run and storage requests), `README.md`
(data source list and the environment variable list), `AGENTS.md` (the second
Scarf account and the fact that the slug is now per-activity, not a single
const), `postgres/postgres_schema.psql`, and a dated migration script under
`postgres/`.

A new repository secret `SCARF_DIAGRID_API_TOKEN` must be added in GitHub before
the first scheduled run, and exported locally before `dapr run -f .`.

## Testing

Unit tests only, matching the existing suite — no network, no database.

**`ScarfPageTests`** — host accepted for `diagrid.io`, `www.diagrid.io`,
`docs.diagrid.io` and their uppercase forms; rejected for `dapr.io`,
`docs.dapr.io`, `localhost`, a preview host and a mirror. `www.` stripped from
`site`. Query string and fragment dropped. Trailing slash stripped, root
preserved as `/`. Mixed case folded. Repeated slashes collapsed. A
percent-encoded `%2F` unescaped to a slash and stored, not rejected. Control
characters rejected. A 513-character path rejected, a 512-character one accepted. Relative
and unparseable referers, and `null`, all rejected.

**`ScarfExportClientTests`** — over `BuildUrl`. The two existing Dapr request
shapes must come out byte-identical to the URLs in the current code, which is
what protects the working collectors through this refactor. Plus: the
`breakdown_set` comma stays unescaped, `group_by_artifact` is absent unless
asked for, multiple `tracking_pixel_id` parameters are emitted, dates format as
`yyyy-MM-dd` under a non-invariant current culture, and the owner slug's
capitalisation is preserved.

`ScarfAggregationResponseTests`, `ScarfRefererTests` and everything else are
untouched and must still pass.

### Manual verification

In this order:

1. Scarf-only run with `SkipStorage: true`. Confirm the two Dapr collectors log
   the same per-week counts as a run captured *before* the refactor — this is
   the regression check on the extraction. Confirm the new collector logs three
   weeks with a plausible page and company count and writes nothing.
2. Scarf-only run with `SkipStorage: false`.
3. The same run again. Row counts in all three Scarf tables must be unchanged,
   which is what proves delete-then-insert is still idempotent.
4. Query `scarf_diagrid_company_pages_view` for the most recent complete week
   and sanity-check the top few companies against the Scarf web UI.

Queries go through Neon's HTTP `/sql` endpoint; there is no `psql` on this
machine.

## Deferred

- **A Diagrid company leaderboard** with an exact per-company
  `unique_visitors`, from a second activity using `breakdown=by-company` and
  `group_by_artifact=false`. It would need either an `account` column on
  `scarf_company_views` — plus that column in the unique constraint, in
  `scarf_top_companies_view`, and in both activities' per-week `DELETE` — or its
  own table. Not needed for the total-page-views question.
- **The `account` column migration on the two Dapr tables**, unblocked by the
  `ScarfExportClient` extraction but not required by it.
- **A top-pages view** mirroring `scarf_top_companies_view`, ranking pages
  within a week. The table supports it; nobody has asked.
