# DataDog RUM weekly collection — design

Date: 2026-08-26

## Problem

User metrics for the dev-dashboard front end live in DataDog RUM and nowhere
else. RUM retains raw session events for 30 days, so any week older than that is
gone permanently — there is currently no long-term record of how many people use
the dev dashboard or how that changes over time.

The rest of the dapr-stats data (packages, images, GitHub, Discord) is collected
into Postgres on a schedule and charted from Grafana. RUM is the only source not
captured that way.

## Goal

Two new Postgres tables, populated as part of the existing `CollectorWorkflow`.

**`datadog_rum`** — two numbers per ISO week per service:

- `session_count` — RUM sessions in that week
- `unique_user_count` — distinct `@usr.anonymous_id` values in that week

Each run fetches the last three *complete* ISO weeks and upserts them.

**`datadog_rum_users`** — a growing registry of every distinct
`@usr.anonymous_id` observed per service, one row per `(service, id)`, never
updated once written:

- `service` — the RUM service the id was seen on
- `user_anonymous_id` — the id itself
- `install_date` — the Monday of the ISO week that id was first seen in, where
  knowable (see below). One-week resolution, deliberately.
- `collection_date` — when this row was first recorded

Both tables are driven by the same `DataDogRumServices` array in the workflow
start payload. Today that array holds `dev-dashboard`; adding a service extends
both tables with no schema change.

Both tables let the Postgres history outlive DataDog's 30-day retention and
accumulate indefinitely.

## Non-goals

- Anything finer than one-week resolution on `install_date`. A day-accurate
  version is possible — one request per day instead of per week — but it costs 30
  requests per service per run instead of 3, for precision nobody asked for.
- Any metric beyond sessions and unique users. No page views, errors, Core Web
  Vitals, or performance percentiles.
- Enabling the other two RUM applications in the org (Conductor UI, Catalyst UI)
  now. Both tables take their services from the `DataDogRumServices` payload
  array, so adding one is a one-line change — but their `service` tag values have
  not been verified, so do not add them blind.
- Postgres views. Unlike every other source, neither table needs rate
  normalisation — see Database schema.
- Resolving anonymous ids to real people. `datadog_rum_users` is a registry of
  browser-generated ids; joining them to accounts is out of scope and would need
  `@usr.id`, which this application does not set.
- A retry/backoff loop of the kind the package collectors use. The overlapping
  lookback already repairs failed runs (see Error handling).
- Backfilling anything older than DataDog's 30-day retention. That data does not
  exist and cannot be recovered.

## Verified facts

Confirmed against the live DataDog org on 2026-08-26 via the DataDog connector,
not assumed:

| Fact | Value |
|---|---|
| Site | US1 — `https://api.datadoghq.com` |
| RUM application | "Dev Dashboard", id `80d4832f-54ab-4091-bd92-0d816379b40a` |
| `service` tag | `dev-dashboard` |
| Session events | `@type:session` exists and is indexed |
| Anonymous id | `@usr.anonymous_id`, populated on every session sampled |
| Environments present | `prod` only |
| Session types present | `user` only (no Synthetics) |
| Volume | 260 sessions, 80 unique users in the last 30 days |
| Event retention | 30 days, hard. The API warns and clamps beyond it. |
| Sample week (Mon 2026-08-17) | 64 sessions, 25 unique anonymous ids |

Two facts that shaped the design:

**DataDog's `interval` bucketing is epoch-anchored, so its weekly buckets start
on Thursday.** A 604800000 ms interval over the last 60 days returned buckets
beginning 2026-07-23, 07-30, 08-06, 08-13, 08-20 — all Thursdays. Weekly
bucketing therefore cannot be delegated to DataDog if the weeks are to be ISO
weeks.

**A week only partly inside the retention window returns a partial count**, not
an error and not zero. Such a row would look like a genuine low week. The
lookback is sized to make this impossible.

Two further findings that shaped the user registry:

**There is no first-seen timestamp for an anonymous id.** The `usr` object on a
session event carries only `anonymous_id` — no creation or install date. And
`MIN` over `@timestamp` grouped by `@usr.anonymous_id` is rejected: the backend
drops the compute and returns a `dropped_computes` warning. A first-seen date can
only be derived, not read.

**Grouping by the id does work**, and the earliest window an id appears in is its
first observed window. Verified with one-day buckets, which returned one row per
id per active day:

```
b7a6a386-...  2026-08-20  7 sessions   <- first observed
b7a6a386-...  2026-08-24  2
b7a6a386-...  2026-08-25  3
```

The same grouping applied to a one-week window gives one row per id per active
week, which is the resolution this design uses.

Either way it is bounded by retention: for an id already active before the
window, the earliest bucket is the oldest retained one, not the truth. See Known
limitations.

## Architecture

Two activities per service, both called from `CollectorWorkflow` and both driven
by the `DataDogRumServices` array in the workflow start payload.

```
CollectorWorkflow
  ├─ GetDataDogRumData(service: "dev-dashboard")
  │    ├─ IsoWeek.CompleteWeeksBefore(utcNow, 3)  ->  3 (from, to) windows
  │    ├─ for each window:
  │    │    POST /api/v2/rum/analytics/aggregate  ->  count + cardinality
  │    │    DataDogRumResponse.Parse(json)
  │    │    upsert into datadog_rum
  │    └─ true
  └─ GetDataDogRumUsers(service: "dev-dashboard")
       ├─ IsoWeek.CompleteWeeksBefore(utcNow, 3)  ->  the same 3 windows
       ├─ for each window, oldest first:
       │    POST /api/v2/rum/analytics/aggregate  (grouped by @usr.anonymous_id)
       │    DataDogRumUsers.ParseIds(json)  ->  ids active that week
       │    firstSeen.TryAdd(id, weekStart)
       ├─ ids whose first week is the oldest week with data -> install_date null
       └─ insert into datadog_rum_users ... on conflict do nothing
```

Both activities walk the **same three ISO week windows** from
`IsoWeek.CompleteWeeksBefore(utcNow, 3)`. One helper, one set of boundaries, and
`install_date` lands on a Monday that always corresponds to a real row in
`datadog_rum` — the two tables are directly joinable on `week_start`.

Both activities fan out over the same `DataDogRumServices` array. There is no
scenario where you want the weekly counts for a service but not its user
registry, so one array drives both.

The two activities are separate because the queries are not interchangeable, even
though they now share their time windows. The weekly counts must be **ungrouped**
— a `group_by` on `@usr.anonymous_id` silently drops any session missing that
attribute, which would undercount `session_count`. The registry must be
**grouped**, and it needs no `cardinality` compute. Merging them would mean
either an undercounted `session_count` or two computes whose grouping semantics
differ.

Week boundaries are computed locally, in UTC: a week runs from Monday
`00:00:00Z` to the following Monday `00:00:00Z`. Only complete weeks are
collected — the in-progress week is skipped, because a partial bar in Grafana
reads as a real decline.

### Why three weeks

Three is the largest lookback that is always entirely inside the 30-day
retention window:

```
current partial week   at most  6 days
3 complete weeks               21 days
                        -------------
worst case                     27 days   < 30  ✔

4 complete weeks would reach 34 days      > 30  -> partial oldest row
```

Three weeks also covers the collection cadence with room to spare. Runs are 7
days apart and each reaches 21 days back, so consecutive runs overlap by two full
weeks. Two runs can fail in a row and the third still refetches every week they
would have covered.

This is the lookback for **both** activities. The registry used to query the full
30-day retention window day by day; at one-week resolution there is nothing to
gain from reaching further back than the weekly counts do, and it drops the
request count from 30 per service to 3.

## Components

### `IsoWeek` (new, static)

```csharp
public static IReadOnlyList<(DateOnly WeekStart, DateTime From, DateTime To)>
    CompleteWeeksBefore(DateTime utcNow, int count)
```

Returns `count` windows, oldest first. `From` is Monday `00:00:00Z`, `To` is the
following Monday `00:00:00Z`, and `WeekStart` is the `DATE` written to Postgres.
The most recent window returned always ends at or before the start of the week
containing `utcNow`.

Pure and side-effect free, so it is unit tested directly with no network or
clock dependency.

### `DataDogRumResponse` (new, static)

```csharp
public static (long SessionCount, long UniqueUserCount) Parse(ReadOnlySpan<byte> json)
```

Expected response shape:

```json
{
  "meta": { "status": "done" },
  "data": {
    "buckets": [
      { "by": {}, "computes": { "c0": 64, "c1": 25 } }
    ]
  }
}
```

Computes are keyed positionally by request order: `c0` is the count, `c1` the
cardinality. A 200 response with an empty `buckets` array means the week
genuinely had no sessions and parses to `(0, 0)` — a real zero week is worth
recording. Anything else (missing `data`, non-numeric computes) throws, and the
activity logs and returns `false`.

> The exact envelope and the `c0`/`c1` key names are taken from the generated
> API client schema, not from a live call. The first implementation step is a
> single `curl` against the live endpoint to confirm both; see Verification.

### `GetDataDogRumData` (new activity)

Modelled on `GetDiagridDashboardData`. Constructor takes `IHttpClientFactory`,
`PostgresOutput` and `DaprClient`. Secrets are read at the top of `RunAsync`, the
way `GetDiscordData` does it:

```csharp
const string secretStore = "secretstore";
const string ApiKeyKey = "DATADOGAPIKEY";
const string AppKeyKey = "DATADOGAPPKEY";
```

Request, per week:

```
POST https://api.datadoghq.com/api/v2/rum/analytics/aggregate
DD-API-KEY: <DATADOGAPIKEY>
DD-APPLICATION-KEY: <DATADOGAPPKEY>
Content-Type: application/json

{
  "compute": [
    { "aggregation": "count",       "type": "total" },
    { "aggregation": "cardinality", "type": "total", "metric": "@usr.anonymous_id" }
  ],
  "filter": {
    "from":  "2026-08-17T00:00:00Z",
    "to":    "2026-08-24T00:00:00Z",
    "query": "@type:session @session.type:user service:dev-dashboard env:prod"
  }
}
```

`compute`, `filter` and `group_by` are top-level keys — the RUM aggregate
request is not wrapped in `data.attributes`. `from`/`to` accept ISO 8601 with a
`Z` suffix. `aggregation` is required; `type` defaults to `total`.

The query carries two filters that are no-ops against today's data:
`@session.type:user` excludes Synthetics traffic, and `env:prod` excludes a
staging deployment. Both exist so that adding staging or a Synthetics test later
cannot silently inflate the numbers.

The base URL is a `const` in the activity, matching how `GetDiagridDashboardData`
hardcodes its URL. US1 is confirmed and the org is not going to move.

Records:

```csharp
public record DataDogRumInput(string Service, bool SkipStorage);
public record DataDogRumData(
    string Service,
    DateOnly WeekStart,
    long SessionCount,
    long UniqueUserCount,
    DateTime CollectionDate);
```

### `DataDogRumUsers` (new, static)

```csharp
public sealed record Entry(string AnonymousId, DateOnly? InstallDate);

// Distinct @usr.anonymous_id values in one day's grouped response.
public static IReadOnlyList<string> ParseIds(ReadOnlySpan<byte> json);

// Weeks must be ordered oldest first. Pure: all the install-date reasoning
// lives here rather than in the activity, so it is testable without HTTP.
public static IReadOnlyList<Entry> DeriveEntries(
    IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<string> Ids)> weeksOldestFirst);
```

**One request per week, with a `total` compute — not a timeseries.** A single
call with a `1w` interval would be fewer requests still, but `interval` turns the
compute into a *timeseries*, so the returned `computes` value stops being a
scalar and becomes a series whose exact JSON encoding this design has not
confirmed. Three explicit week windows instead reuse the identical
`buckets[].by` / `buckets[].computes` shape as the weekly-count query, which
means one verified response shape for the whole feature and no timeseries parsing
at all — for two extra HTTP calls.

Per-week request, using the same `from`/`to` as the matching `datadog_rum` row:

```
{
  "compute":  [ { "aggregation": "count", "type": "total" } ],
  "group_by": [ { "facet": "@usr.anonymous_id", "limit": 1000 } ],
  "filter": {
    "from":  "2026-08-17T00:00:00Z",
    "to":    "2026-08-24T00:00:00Z",
    "query": "@type:session @session.type:user service:dev-dashboard env:prod"
  }
}
```

### First-seen derivation

The activity walks the weeks **oldest first**, so the first week an id appears in
is its earliest observed week:

```csharp
// weekStart -> ids, oldest week first
foreach (var week in weeks)
    foreach (var id in ParseIds(response))
        firstSeen.TryAdd(id, week.WeekStart);   // never overwritten by a later week
```

`install_date` is that Monday, except for ids whose first week is the **oldest
week that returned any data at all**. Those get `null`: an id present in the
earliest week of the window was probably active before it, so its true first week
is unknowable. Deriving that boundary from the responses themselves avoids
parsing DataDog's retention warning text.

With weekly runs this `null` case is almost entirely a first-run artefact. A week
is first collected when it is the *newest* of the three, so it gets a real
`install_date` then, and `do nothing` preserves that value when the same week
later becomes the oldest of the three. Only ids whose first appearance falls in
the oldest week of the very first run are permanently `null` — which is correct,
because collection had not started yet.

### `GetDataDogRumUsers` (new activity)

Same construction as `GetDataDogRumData` — `IHttpClientFactory`,
`PostgresOutput`, `DaprClient`, the same two secrets.

It takes the same input record as `GetDataDogRumData`:

```csharp
public record DataDogRumInput(string Service, bool SkipStorage);
```

One record serves both activities, and the workflow fans both out over
`DataDogRumServices`. The service reaches the query as `service:{input.Service}`
and is stored on every row, so the registry stays partitioned by service even
though the ids themselves are globally unique UUIDs.

Three requests per run, one per complete ISO week, oldest first, using the
per-week body shown under `DataDogRumUsers` above. The windows come from
`IsoWeek.CompleteWeeksBefore(utcNow, 3)` — the same call `GetDataDogRumData`
makes.

The `limit` of 1000 on the grouping is DataDog's default maximum. Current volume
is 80 distinct ids across 30 days, so a single week is nowhere near it. The
activity logs a warning if any week returns exactly 1000 ids, because that is the
signal that ids are being silently dropped.

A week whose request fails is logged and skipped, and the run continues. One
missing week can only cost precision on `install_date` for ids first seen that
week, and the next run refetches it anyway; it cannot corrupt an already-stored
row, because of `do nothing`.

### `Program.cs`

Register both activities alongside the others:

```csharp
options.RegisterActivity<GetDataDogRumData>();
options.RegisterActivity<GetDataDogRumUsers>();
```

No new DI registrations — `IHttpClientFactory`, `PostgresOutput` and `DaprClient`
are already registered.

## Database schema

```sql
CREATE TABLE datadog_rum (
    id SERIAL PRIMARY KEY,
    service VARCHAR(255) NOT NULL,
    week_start DATE NOT NULL,
    session_count BIGINT NOT NULL,
    unique_user_count BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT datadog_rum_service_week_unique UNIQUE (service, week_start)
);
```

Added to `postgres/postgres_schema.psql` following the existing table
conventions (`SERIAL` primary key, `collection_date TIMESTAMP`).

Upsert, issued through `PostgresOutput.InsertAsync`:

```sql
insert into datadog_rum
    (service, week_start, session_count, unique_user_count, collection_date)
values ($1, $2, $3, $4, $5)
on conflict (service, week_start) do update
   set session_count     = excluded.session_count,
       unique_user_count = excluded.unique_user_count,
       collection_date   = excluded.collection_date
```

The unique constraint is what makes the overlapping lookback safe: re-collecting
a week overwrites it with the same or better data rather than duplicating it.

`collection_date` is `DateTime.UtcNow` at insert time, consistent with
`GetDiscordData` and `GetDiagridDashboardData`. It records when the row was
written, not what it measures — `week_start` is the meaningful axis.

### `datadog_rum_users`

```sql
CREATE TABLE datadog_rum_users (
    id SERIAL PRIMARY KEY,
    service VARCHAR(255) NOT NULL,
    user_anonymous_id VARCHAR(64) NOT NULL,
    install_date DATE,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT datadog_rum_users_service_user_unique UNIQUE (service, user_anonymous_id)
);
```

`install_date` is the only nullable column, and deliberately so — see
`DataDogRumUsers` above and Known limitations below.

The unique key is `(service, user_anonymous_id)`, matching `datadog_rum`'s
`(service, week_start)`. Anonymous ids are per-application UUIDs, so a collision
across services is not a practical concern and the composite key behaves like a
bare one today. What it buys is that adding a service needs no migration, and
that `install_date` is tracked independently per service — the same browser
visiting two Diagrid front ends gets a separate first-seen date on each, which is
the honest reading of "first seen on this service".

Counting distinct ids across all services is then
`select count(distinct user_anonymous_id) from datadog_rum_users`.

```sql
insert into datadog_rum_users
    (service, user_anonymous_id, install_date, collection_date)
values ($1, $2, $3::date, $4)
on conflict (service, user_anonymous_id) do nothing
```

**`do nothing`, not `do update`.** This is the opposite choice from
`datadog_rum`, and it is what makes the table trustworthy: once an id is
recorded, neither its `install_date` nor its `collection_date` is ever
overwritten. A later run seeing the same id has strictly worse information about
when it first appeared — its view of history is shorter. `collection_date`
therefore means "when we first recorded this id", not "when we last saw it".

The table only grows. At 80 distinct ids per 30 days it will take years to reach
a size worth thinking about.

### No views

Every other source stores a cumulative counter and needs a view to turn
successive readings into a rate. RUM sessions are events already scoped to a
week, so `datadog_rum` is its own final shape; `datadog_rum_users` is a registry.
Grafana queries both tables directly.

### `unique_user_count` is not additive

A person who visits in two different weeks is counted once in each. Summing the
column across weeks does not give unique users for the period, and any
"unique users this month" panel needs its own DataDog query over that month
rather than a `SUM` over rows.

Both sides of this were measured during design. Five consecutive weekly
cardinalities were 18, 22, 17, 19 and 22, summing to 98. A single query over the
same 30-day span returned 80. The sum overstates the real figure by 22%, and the
gap widens the more people return week to week.

## Workflow changes

`CollectorWorkflowInput` gains one field:

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
    bool SkipStorage);
```

In `RunAsync`, after the Diagrid dashboard block:

```csharp
if (input.DataDogRumServices?.Length > 0)
{
    var rumTasks = new List<Task>();
    foreach (var service in input.DataDogRumServices)
    {
        rumTasks.Add(context.CallActivityAsync(
            nameof(GetDataDogRumData),
            new DataDogRumInput(service, input.SkipStorage)));
        rumTasks.Add(context.CallActivityAsync(
            nameof(GetDataDogRumUsers),
            new DataDogRumInput(service, input.SkipStorage)));
    }
    await Task.WhenAll(rumTasks);
}
```

Both activities take the same `DataDogRumInput`, so one record and one array
serve both. Adding a service to the payload extends both tables at once.

Request volume is modest: each service costs 3 requests for the weekly counts
plus 3 for the registry, so **6 per service per run** — 6 today, 24 if all four
Diagrid front ends were onboarded. The NuGet loop alone already makes about
fifty.

The null-conditional is deliberate. Existing array fields are dereferenced
directly, but `local-tests.http` contains payloads that will not list the new
field, and those requests must keep working rather than fail with a
`NullReferenceException`.

Services run concurrently. Only one is configured today, and three weeks × N
services is far too few calls to approach a DataDog rate limit.

## Secrets and CI changes

Two new secrets, following the existing env-backed secret store:

- `DATADOGAPIKEY` — a DataDog API key
- `DATADOGAPPKEY` — a DataDog **Application** key. RUM analytics requires this
  in addition to the API key; the API key alone returns 403.

Changes:

- `CollectDaprStats/secrets.json.example` — add both keys, empty.
- `.github/workflows/run-workflow.yaml` — add both to the `env:` block, sourced
  from repository secrets, add `"DataDogRumServices": ["dev-dashboard"]` to the
  workflow start payload, and change the schedule (below).
- `local-tests.http` — add the same field to the full collection request. The
  existing targeted requests are left alone: an absent `DataDogRumServices` is
  handled by the `?.` guard in the workflow.

## Schedule change

The collection cron moves from twice monthly to weekly:

```yaml
# was: 10:00 CET (09:00 UTC) on day 1 and 16 of each month
- cron: '0 9 1,16 * *'

# now: 10:00 CET (09:00 UTC) every Monday
- cron: '0 9 * * 1'
```

Three deliberate choices in those five fields:

**Day-of-week, not day-of-month.** `1,8,15,22` looks weekly but is not: it leaves
a 29- to 31-day gap across every month boundary. Only the day-of-week field gives
exactly seven days between runs.

**Monday.** ISO weeks close at Monday `00:00:00Z`, so a Monday run collects a week
that closed nine hours earlier — the shortest possible lag between a week ending
and being recorded. Any other weekday would leave the newest complete week
sitting uncollected for up to six extra days.

**09:00 UTC, unchanged.** Keeps the existing 10:00 CET slot and the comment that
documents it.

This is a change to the *whole* collection workflow, not just the RUM part. Every
other collector starts running weekly too. **No view needs changing and no
historical row is invalidated**, but the collectors divide into two kinds and only
one of them adapts on its own.

**Views that derive their divisor from the gap between runs** — the nine NuGet
views, `dockerhub_images_view`, `diagrid_dashboard_view` — follow the cadence
automatically. Their `days_diff` drops from about 15 to about 7, which raises the
resolution of every increment series. This is the divisor the `EXTRACT(DAY ...)`
truncation fix made accurate in the first place.

**Views that divide by a stored `collected_over_number_of_days`** depend on a
constant in the collector, which does *not* track the cron:

| Collector | Window | Under a 15-day cadence | Under a 7-day cadence |
|---|---|---|---|
| npm (`GetNpmPackageData.cs`) | 7 days | 8-day blind spot each cycle | tiles exactly |
| GitHub (`GetGitHubRepoData.cs`) | 15 days | tiles exactly | 8-day overlap |
| PyPI (`GetPythonPackageData.cs`) | 30 days | 2x overlap | ~4.3x overlap |

npm improves for free: at 7-day windows sampled every 15 days, 8 of every 15 days
were never counted at all.

GitHub needs a one-line change — `CollectionPeriodInDays` from 15 to 7 — or
consecutive rows would double-count the same commits, issues and pull requests.
Nothing breaks without it, because the view divides by each row's own stored
window and still yields a correct weekly rate; but the series would change
character from discrete buckets into a 15-day moving average sampled weekly. That
constant and the cron interval are coupled from here on.

PyPI cannot be fixed the same way: 30 days is what the PyPI API returns, not a
choice. It was already a moving average and simply gets smoother.

Because each row stores the window it was collected over, rows written before and
after this change stay directly comparable — old rows divide by 15, new rows by 7,
and both are correct weekly rates.

## Error handling

Non-2xx responses and unparseable bodies are logged and cause the activity to
return `false`, matching every other collector in the project. The workflow does
not inspect the result.

There is deliberately no retry loop. The three-week overlapping lookback means a
failed run costs nothing: the next run refetches the same weeks and upserts them.
The Get→Check→backoff machinery the package collectors need exists because a
missed package leaves a permanent hole in a cumulative series; here it would be
dead weight for three HTTP calls a fortnight.

A partial failure — some weeks succeeding, a later one failing — leaves the
successful weeks committed, which is correct. Each week is independent.

## Verification

1. **Confirm the request shape.** One `curl` against
   `/api/v2/rum/analytics/aggregate` with the body above, checking that
   lowercase `count`/`cardinality` are accepted and that computes come back as
   `c0`/`c1`. Expected for the week of 2026-08-17: **64 sessions, 25 unique
   users** — the numbers the connector returned during design. That week leaves
   the retention window around 2026-09-16; after that, re-measure a recent week
   with the DataDog UI and compare against it instead. Capture the real response
   body and use it as the `DataDogRumResponse.Parse` test fixture.
   Capture a grouped response for the same week in the same pass, to confirm that
   `buckets[].by` carries the `@usr.anonymous_id` value under that exact key.
   That is the only additional unknown, since the grouped request reuses the same
   `type: "total"` compute shape and the same window as the weekly one.
2. **Unit tests** (`CollectDaprStats.Tests`, xunit, matching
   `PackageDataCheckerTests` style — pure functions, no network):
   - `IsoWeek`: a midweek `utcNow`; exactly Monday `00:00:00Z`; a year boundary;
     and the invariant that no returned window overlaps the week containing
     `utcNow`.
   - `DataDogRumResponse.Parse`: the captured response; an empty `buckets` array
     parsing to `(0, 0)`; a malformed body throwing.
   - `DataDogRumUsers.ParseIds`: the captured grouped response; a response with
     no buckets; a bucket missing the facet being skipped; a malformed body
     throwing.
   - `DataDogRumUsers.DeriveEntries`: an id present in the oldest week yielding
     `null`; an id first appearing in a later week yielding that Monday; an id
     appearing in several weeks keeping the earliest; empty leading weeks not
     counting as the boundary.
3. **Local run with `SkipStorage: true`** — logs three weeks of numbers and a
   count of distinct ids, writes nothing. Cross-check the weekly values against
   the DataDog RUM Explorer UI for the same weeks, and the id count against a
   `cardinality` query over the same three weeks.

   `unique_user_count` for the newest week in `datadog_rum` and the number of ids
   the registry saw in that same week must agree — they are the same set counted
   two ways, one by DataDog's `cardinality` and one by counting grouped buckets.
   A mismatch means the two activities are not looking at the same window.
4. **Local run with `SkipStorage: false`** — three rows in `datadog_rum`, and one
   row per distinct id in `datadog_rum_users`.
5. **Idempotency** — run twice. `datadog_rum` still has three rows with
   `collection_date` advanced and counts unchanged. `datadog_rum_users` has the
   same row count as after the first run, with **`install_date` and
   `collection_date` unchanged on every row** — this is what proves `do nothing`
   rather than `do update` is in effect.
6. **Scheduled run** — trigger `run-workflow.yaml` via `workflow_dispatch` and
   confirm rows appear in both tables with the GH Actions secrets wired up.

## Known limitations

- **History starts now.** Weeks before roughly 2026-07-27 are outside DataDog's
  retention and are permanently unavailable. The series begins with whatever the
  first run can reach.
- **An outage longer than three weeks loses data, even though DataDog still has
  it.** The collector only ever asks for the last three complete weeks, so if no
  run succeeds for longer than that, the un-reached weeks are never collected —
  they are still inside DataDog's 30-day retention for a while, but nothing goes
  back for them. Nothing detects this either; the table simply has a gap. At the
  weekly cadence that means three consecutive failed runs. Recovering such a gap
  means a manual one-off run with a larger lookback, while the data is still
  retained.
- **Sampling is not accounted for.** The RUM SDK is configured with
  `session_sample_rate: 100` today, so counts are complete. If that rate is ever
  lowered, stored counts become samples and the change will be invisible in the
  table — there is no column recording the sample rate.
- **`unique_user_count` counts browsers, not people.** `@usr.anonymous_id` is
  client-side state, so one person on two devices counts twice, and clearing site
  data creates a new id. The same caveat applies to every row of
  `datadog_rum_users`: it is a registry of browsers, not of humans.
- **`install_date` has one-week resolution and means "first observed".** There is
  no install or account-creation timestamp in RUM; the value is the Monday of the
  earliest ISO week in which the id appears. Someone first visiting on a Thursday
  is recorded against that week's Monday. Four consequences, in decreasing
  severity:
  - For every id already active when collection starts, the real first date is
    before the retention window and unknowable. Those rows get `NULL`.
  - A returning visitor can be dated wrongly in the other direction: if someone
    was active 60 days ago, dormant, then returned last week, the earliest week
    they appear in is last week and that becomes their install date. The
    oldest-week check cannot catch this — they are absent from the oldest week
    entirely. Rows written in the first weeks of collection should be treated as
    approximate.
  - The recorded Monday can precede the person's actual first visit by up to six
    days. That is the accepted cost of one-week resolution.
  - Accuracy improves permanently over time. Any id first appearing after
    collection is running lands within the correct day, and `do nothing` means
    that value is never degraded by a later run.
- **An outage longer than three weeks loses ids entirely**, not just their install
  dates. An anonymous id active only during the gap never enters the registry
  unless that browser returns later — in which case it is recorded with the wrong,
  later install date.
- **Long-horizon session history is possible but not included.** RUM-based custom
  metrics retain ~15 months and would break the 30-day ceiling for
  `session_count`. They cannot express a distinct count, so `unique_user_count`
  would still be limited to 30 days, and the metric definition would live in the
  DataDog UI rather than in this repo. Worth revisiting only if the retention
  ceiling becomes a real problem.
