# DataDog identified-user weekly collection — design

Date: 2026-09-02

## Problem

`conductor-ui` is a signed-in product. Its users have accounts, and DataDog RUM
records who they are on every session — `@usr.id`, `@usr.email`, `@usr.name`.
None of that is captured anywhere durable. RUM keeps raw session events for 30
days, so any week older than that is gone permanently: there is no long-term
record of how many people use Conductor, who they are, or how the user base
grows.

The existing `datadog_rum` / `datadog_rum_users` pair was built for
`dev-dashboard`, which has no sign-in at all. It counts distinct
`@usr.anonymous_id` values, and that measure does not transfer: on
`conductor-ui` the anonymous id is effectively per-session (see Verified
facts), so its cardinality counts visits, not people.

## Goal

Two new Postgres tables plus one view, populated by the existing
`CollectorWorkflow`, giving three numbers per ISO week per `(service, env)`
pair.

**`datadog_rum_identified`** — the weekly aggregate:

- `session_count` — authenticated RUM sessions that week (sessions carrying an
  `@usr.id`)
- `unique_user_count` — distinct `@usr.id` values that week

**`datadog_rum_identified_users`** — a growing registry of every distinct
`@usr.id` observed per `(service, env)`:

- `user_id` — the account id
- `user_email`, `user_name` — the most recently observed values, nullable
- `install_date` — the Monday of the ISO week the id was first seen in, where
  knowable
- `collection_date` — when the row was first recorded

**`datadog_rum_identified_users_view`** — the running total of unique users,
derived from the registry rather than collected. Nothing extra is fetched for
it, and it stays consistent with the weekly numbers by construction.

Each run fetches the last three *complete* ISO weeks and upserts them. The two
`(service, env)` pairs collected are:

```
conductor-ui | conductor.r1.diagrid.io
conductor-ui | dapr-ops-dashboard.diagrid.io
```

Both tables let the Postgres history outlive RUM's 30-day retention and
accumulate indefinitely.

## Non-goals

- **Unauthenticated session counts.** 78% of `conductor-ui` production sessions
  carry no `@usr.id`. They are login-page and pre-auth traffic, and counting
  them would swamp the signal. Deliberately discarded, and unrecoverable after
  30 days — this is the one non-goal with a permanent cost.
- **`catalyst-ui`.** Same event shape, so adding it later is a one-line config
  change and no code change. Out of scope now.
- **Staging and local environments.** `conductor.staging.diagrid.dev`,
  `conductor.local.diagrid.io` and the App Runner host are not collected.
- **Day-level resolution on `install_date`.** One-week resolution, deliberately,
  for the same reason as the `dev-dashboard` design: day accuracy costs 30
  requests per pair per run instead of 3.
- **Backfill beyond RUM retention.** History starts at first collection. The
  first week will look like a large acquisition spike; it is a cold-start
  artifact.
- **Org or tenant attributes.** They do not exist on these events (measured, see
  below).
- **An erasure deny-list.** See Privacy constraints. Documented, not built.
- **Any metric beyond sessions and users.** No page views, errors, Core Web
  Vitals, or performance percentiles.

## Verified facts

Measured 2026-09-02 through the DataDog RUM aggregate API over a 28-day window.
RUM retention is 30 days, so a wider window is clamped and the numbers shift
slightly between probes; the 28-day figures are the ones quoted here.

**RUM services in the org, and which carry an identity:**

| service | sessions | `@usr.id` | `@usr.email` | `@usr.anonymous_id` |
|---|---|---|---|---|
| `catalyst-ui` | 4974 | 124 | 105 | 2261 |
| `conductor-ui` | 1535 | 71 | 67 | 1126 |
| `dev-dashboard` | 324 | — | — | 77 |

`dev-dashboard` populates no identity attributes at all. `conductor-ui` and
`catalyst-ui` do.

**`env` is a hostname on `conductor-ui`, not `prod`.** The existing collector's
`env:prod` filter matches nothing here. Values seen:

| `env` | sessions | `@usr.id` | `@usr.email` |
|---|---|---|---|
| `conductor.r1.diagrid.io` | 1449 | 66 | 65 |
| `conductor.staging.diagrid.dev` | 39 | 6 | 6 |
| `dapr-ops-dashboard.diagrid.io` | 39 | 3 | 3 |
| `z2rbwrqxsq.eu-west-1.awsapprunner.com` | 22 | 2 | 2 |
| `conductor-prd-57949.web.app` | 1 | — | — |
| `conductor.local.diagrid.io` | 1 | 1 | 1 |

`dapr-ops-dashboard.diagrid.io` reports under the `conductor-ui` service. It is
a production surface and is collected as its own row.

**`@usr.anonymous_id` is not an identity on `conductor-ui`.** 1411 production
sessions produced 1020 distinct anonymous ids — roughly one per session. On
`dev-dashboard` 324 sessions produced 77. Whatever persists the id on the dev
dashboard does not persist it here, so its cardinality is not a user count.

**78% of production sessions are unauthenticated.** Of 1411 sessions on
`conductor.r1.diagrid.io`, 1095 carry no `@usr.id`.

**Identity attribute coverage on authenticated production sessions:** 63
distinct `@usr.id`, 62 distinct `@usr.email`, 62 distinct `@usr.name`. At least
one id therefore has no email, which is why `user_email` is nullable and why the
group-by question below has to be settled.

**Attributes that do not exist:** `@usr.organization_id`, `@usr.org_id`,
`@usr.role`, `@usr.tenant` all return no values. There is no tenant dimension
available.

**Internal accounts are a fifth of the user base.** On the production host, 14
of 63 users and 79 of 1411 sessions come from `@diagrid.io` addresses.

**All sessions are `@session.type:user`.** No Synthetics traffic on either host
today. The filter is kept anyway, so a future Synthetics test cannot silently
inflate the counts.

**The repository is public** (`gh repo view` → `"visibility":"PUBLIC"`), so
GitHub Actions run logs are world-readable. This drives the Privacy constraints
section.

## Architecture

The `dev-dashboard` shape is mirrored: two activities per `(service, env)` pair,
one writing the weekly aggregate and one maintaining the user registry, both
driven from `CollectorWorkflow` and both fetching the same three complete ISO
weeks.

```
CollectorWorkflow
  └─ for each "service|env" in DataDogRumIdentifiedServices
       ├─ GetDataDogRumIdentifiedData
       │    └─ for each of the last 3 complete ISO weeks
       │         ├─ POST /api/v2/rum/analytics/aggregate
       │         │    computes: count(total), cardinality(@usr.id)
       │         └─ upsert one datadog_rum_identified row
       └─ GetDataDogRumIdentifiedUsers
            ├─ for each of the last 3 complete ISO weeks
            │    └─ POST /api/v2/rum/analytics/aggregate
            │         group_by: @usr.id, @usr.email, @usr.name
            ├─ derive one entry per distinct id
            │    ├─ install_date = earliest week the id appeared in
            │    ├─ email/name  = newest non-null values seen
            │    └─ ids first seen in the oldest week with data -> install_date null
            └─ upsert one datadog_rum_identified_users row per entry
```

Both activities share the DataDog search query, which is the one thing that must
stay in step between them:

```
@type:session @session.type:user service:{Service} env:{Env} @usr.id:*
```

`@usr.id:*` is what makes "authenticated sessions only" true.
`@session.type:user` excludes Synthetics. `env:{Env}` is an exact host match,
not a prefix.

### Why new tables instead of an `env` column

Adding `env` to `datadog_rum` and `datadog_rum_users` would mean dropping and
recreating a unique constraint on a production table, and it would leave
`unique_user_count` meaning two different things depending on the row: a
distinct browser for `dev-dashboard`, a distinct account for `conductor-ui`.
Separate tables need no migration and keep the semantics honest. The cost is one
extra Grafana panel instead of one panel with two series, which is the cheaper
side of the trade.

`dev-dashboard` collection is untouched by this work. No existing table, view,
activity or workflow input changes behaviour.

### Why authenticated sessions only

The alternative was storing both a total and an authenticated count, since both
come back from one request. Authenticated-only was chosen: the number then means
"sessions by people with accounts", which is the question being asked, and the
column has one meaning rather than two. The consequence is stated in Non-goals —
the unauthenticated 78% is discarded permanently, week by week, as retention
expires.

### Why three weeks

Unchanged from the `dev-dashboard` design, and the reasoning is worth repeating
because it is easy to "improve" wrongly. Three complete ISO weeks is the largest
lookback always inside the 30-day window: at most 6 days of the current partial
week plus 21 days of complete weeks is 27 days. Four weeks reaches 34 days, and
a week only partly inside retention returns a partial count that is
indistinguishable from a genuinely quiet week.

The current in-progress week is never collected, so no partial bar appears in
Grafana.

### The group-by question to settle first

The MCP tool documentation for RUM aggregation states that events missing a
group-by field are dropped from the results. The comment in the existing
`DataDogRumUsers.cs` states the opposite — that they arrive as a bucket with the
facet key absent. Both cannot be true, and the difference decides whether the
one production user who has an `@usr.id` but no `@usr.email` appears in the
registry or vanishes from it.

This is settled empirically as the first implementation step, not guessed at.
Run the three-facet request and a `@usr.id`-only request over the same week and
compare their distinct-id counts:

- **Equal** — buckets keep the id when an attribute is missing. One request per
  week stands.
- **Different** — the three-facet request drops those events. Fall back to two
  requests per week: one grouped by `@usr.id` alone for the complete id set, one
  grouped by all three facets for attributes, merged in code by id. Ids with no
  attribute row keep null email and name.

Worst case is 12 registry requests plus 6 aggregate requests per run, which is
negligible next to the package collection loops.

## Components

### `DataDogRumResponse` (reused unchanged)

The weekly aggregate response has the same shape as the `dev-dashboard` one —
`c0` a count, `c1` a cardinality, keyed positionally by request order. `Parse`
is reused as-is, including its "empty buckets array is a real zero week"
behaviour. No new parsing code and no new tests for the aggregate path.

### `DataDogRumIdentifiedUsers` (new, static)

Parsing and derivation for the identified-user registry. A sibling to
`DataDogRumUsers` rather than a generalisation of it: the existing class is
small, and it backs a collector that is running in production weekly. Keeping
them separate means this work cannot destabilise `dev-dashboard` collection.

```csharp
public sealed record Observation(string Id, string? Email, string? Name);
public sealed record Entry(
    string UserId, string? Email, string? Name, DateOnly? InstallDate);

public static IReadOnlyList<Observation> ParseUsers(ReadOnlySpan<byte> json);
public static IReadOnlyList<Entry> DeriveEntries(
    IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Users)> weeksOldestFirst);
```

`ParseUsers` walks `data.buckets[].by`, reading `@usr.id`, `@usr.email` and
`@usr.name`. A bucket with no `@usr.id` is skipped — there is nothing to
register. A bucket with an id but no email or name yields an `Observation` with
nulls, which is the case the group-by question above is about.

Failure modes match the existing parser exactly: an empty body and malformed
JSON each throw `FormatException`; a missing `data.buckets` array throws; an
empty `buckets` array yields an empty list.

### First-seen and attribute derivation

`DeriveEntries` takes the weeks oldest-first, which is the order
`IsoWeek.CompleteWeeksBefore` already returns, and makes one pass:

- `install_date` — `TryAdd` into a dictionary keyed by id, so the **earliest**
  week an id appeared in wins and later weeks cannot move it.
- `user_email` / `user_name` — overwritten on each pass **only when the observed
  value is non-null**, so the newest known value wins and a week where the
  attribute happened to be absent cannot erase one that is known.
- Boundary rule — an id whose first week is the oldest week that returned *any*
  data gets a null `install_date`. That week is the edge of what was queried, so
  the id was probably active before it and its true first week is unknowable.
  A leading week that returned nothing is not the boundary; the boundary is the
  oldest week that actually produced users.

The null `install_date` is not a gap to be filled at write time. The view seeds
those users into the earliest week on record, which is what makes the running
total start from a truthful baseline rather than from zero.

### `GetDataDogRumIdentifiedData` (new activity)

`WorkflowActivity<DataDogRumIdentifiedInput, bool>`. Reads `DATADOGAPIKEY` and
`DATADOGAPPKEY` from the `secretstore`, iterates the three complete weeks, and
for each one POSTs:

```json
{
  "compute": [
    { "aggregation": "count", "type": "total" },
    { "aggregation": "cardinality", "type": "total", "metric": "@usr.id" }
  ],
  "filter": {
    "from": "<week start, ISO 8601 Z>",
    "to": "<week end, ISO 8601 Z>",
    "query": "@type:session @session.type:user service:conductor-ui env:conductor.r1.diagrid.io @usr.id:*"
  }
}
```

Each week is wrapped in its own `try`/`catch` so one failure cannot discard the
others, and the activity returns whether every week succeeded. Every
`InsertAsync` sits behind `if (!input.SkipStorage)`.

### `GetDataDogRumIdentifiedUsers` (new activity)

`WorkflowActivity<DataDogRumIdentifiedInput, bool>`. Same secrets, same three
weeks, same per-week `try`/`catch`, but grouped rather than aggregated:

```json
{
  "compute": [{ "aggregation": "count", "type": "total" }],
  "group_by": [
    { "facet": "@usr.id", "limit": 1000 },
    { "facet": "@usr.email", "limit": 1000 },
    { "facet": "@usr.name", "limit": 1000 }
  ],
  "filter": { "from": "...", "to": "...", "query": "<same query>" }
}
```

`limit` is DataDog's default maximum for field grouping. Current volume is ~63
distinct users per week on the busier host, so the limit is far away — but the
activity logs a warning when a week returns bucket counts at the limit, exactly
as `GetDataDogRumUsers` does, because silently truncated ids would corrupt the
registry rather than merely dent it.

A failed week costs precision on `install_date` for ids first seen that week and
nothing else; the next run refetches it. It cannot corrupt a stored row, because
the write never moves `install_date`.

### `DataDogRumIdentifiedInput` (new record)

```csharp
public record DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage);
```

`SkipStorage` last, per repository convention.

### `Program.cs`

No change. Activities are discovered by the parameterless `AddDaprWorkflow()`
with no explicit registration, and the three constructor dependencies —
`IHttpClientFactory`, `PostgresOutput`, `DaprClient` — are already in the
container.

## Database schema

Both tables go in `postgres/postgres_schema.psql` alongside the existing
`datadog_rum` pair.

### `datadog_rum_identified`

```sql
CREATE TABLE datadog_rum_identified (
    id SERIAL PRIMARY KEY,
    service VARCHAR(255) NOT NULL,
    env VARCHAR(255) NOT NULL,
    week_start DATE NOT NULL,
    session_count BIGINT NOT NULL,
    unique_user_count BIGINT NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT datadog_rum_identified_week_unique
        UNIQUE (service, env, week_start)
);
```

`session_count` is authenticated sessions only. `env` is part of the unique
constraint, which is what keeps the two production hosts as independent series
rather than merging them.

### `datadog_rum_identified_users`

```sql
CREATE TABLE datadog_rum_identified_users (
    id SERIAL PRIMARY KEY,
    service VARCHAR(255) NOT NULL,
    env VARCHAR(255) NOT NULL,
    user_id VARCHAR(128) NOT NULL,
    user_email VARCHAR(320),
    user_name VARCHAR(255),
    install_date DATE,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT datadog_rum_identified_users_unique
        UNIQUE (service, env, user_id)
);
```

`user_email` is nullable because at least one production id has no email, and
`user_name` for the same reason. `install_date` is nullable by design — see the
boundary rule above. `VARCHAR(320)` is the practical maximum email length
(64-character local part, `@`, 255-character domain).

`VARCHAR(128)` on `user_id` is an assumption, not a measured value — the ids
were deliberately never printed during design, so their length is unverified.
128 is generous for any UUID or opaque account id. If it is ever too short the
insert fails loudly rather than truncating, which is the right failure and is
fixed by widening the column.

The unique constraint includes `env`, so the same account signing into both
Conductor and the ops dashboard is tracked independently per surface. That is
deliberate: the question being answered is per-surface adoption.

### `datadog_rum_identified_users_view`

The running total. The `install_date IS NULL` users are seeded into the earliest
week on record, because dropping them would undercount the running total by the
entire population that existed when collection began.

```sql
CREATE OR REPLACE VIEW datadog_rum_identified_users_view AS
WITH first_week AS (
    SELECT service, env, MIN(week_start) AS week_start
    FROM datadog_rum_identified
    GROUP BY service, env
),
seeded AS (
    SELECT u.service,
           u.env,
           COALESCE(u.install_date, f.week_start) AS week_start,
           u.user_email
    FROM datadog_rum_identified_users u
    JOIN first_week f
      ON f.service = u.service AND f.env = u.env
)
SELECT service,
       env,
       week_start,
       COUNT(*) AS new_users,
       COUNT(*) FILTER (
           WHERE user_email IS NULL OR user_email NOT LIKE '%@diagrid.io'
       ) AS new_external_users,
       SUM(COUNT(*)) OVER (
           PARTITION BY service, env ORDER BY week_start
       ) AS total_users,
       SUM(COUNT(*) FILTER (
           WHERE user_email IS NULL OR user_email NOT LIKE '%@diagrid.io'
       )) OVER (
           PARTITION BY service, env ORDER BY week_start
       ) AS total_external_users
FROM seeded
GROUP BY service, env, week_start;
```

Three properties of this view are load-bearing:

- **The internal/external split lives here**, not in the collector. Internal
  accounts are collected like any other, so the domain rule can change without
  a backfill and without having lost data.
- **A user with a null email counts as external.** There is one such id today.
  Treating unknown as internal would understate customer numbers.
- **This is the intended Grafana surface.** Panels read counts from the view
  rather than selecting raw email addresses out of the table.

The `INNER JOIN` means a user is invisible until the aggregate table has at
least one week for that `(service, env)`. In practice both activities run in the
same workflow, so this only shows during a partial first run.

### `unique_user_count` is not additive

Summing `unique_user_count` across weeks double-counts anyone active in more
than one week. Grafana must not aggregate that column across time. The running
total exists precisely so that nobody is tempted to.

`total_users` is likewise **not** a count of live accounts: it is distinct
accounts ever observed since collection began, and churned users are never
subtracted. That is the intended meaning of "running total of unique users".

## Write strategy

**Aggregate table — upsert, overwriting:**

```sql
insert into datadog_rum_identified
  (service, env, week_start, session_count, unique_user_count, collection_date)
values ($1, $2, $3::date, $4, $5, $6)
on conflict (service, env, week_start) do update
set session_count     = excluded.session_count,
    unique_user_count = excluded.unique_user_count,
    collection_date   = excluded.collection_date
```

The overlapping three-week lookback is what makes this safe and useful: a week
re-collected within retention is complete both times, so overwriting corrects
rather than duplicates.

**Registry — upsert, attributes only:**

```sql
insert into datadog_rum_identified_users
  (service, env, user_id, user_email, user_name, install_date, collection_date)
values ($1, $2, $3, $4, $5, $6::date, $7)
on conflict (service, env, user_id) do update
set user_email = coalesce(excluded.user_email, datadog_rum_identified_users.user_email),
    user_name  = coalesce(excluded.user_name,  datadog_rum_identified_users.user_name)
```

This is the one deliberate deviation from the `dev-dashboard` registry, which
uses `on conflict do nothing`. `do nothing` is right for an anonymous id that
carries no attributes; here it would freeze a stale email address forever, since
an email can change while the account id does not.

What the deviation must not break is the reason `do nothing` was chosen
originally: a later run has a strictly shorter view of history, so it must never
overwrite a recorded first date. Hence `install_date` and `collection_date` are
absent from the `do update` list. They are written once and never again.

The `coalesce` is a second line of defence behind the derivation's
newest-non-null rule: even if a null reached the insert, it cannot erase a known
value.

## Workflow changes

`CollectorWorkflowInput` gains one field:

```csharp
string[] DataDogRumIdentifiedServices
```

holding `service|env` pairs:

```json
["conductor-ui|conductor.r1.diagrid.io",
 "conductor-ui|dapr-ops-dashboard.diagrid.io"]
```

The workflow splits each entry on `|`, the way `DockerHubImages` splits on `/`,
skips entries that do not yield exactly two parts, and guards the block with
`.Length > 0`. An empty array means "skip this source" — the GitHub Actions
checkboxes depend on that, and the existing `DataDogRumServices` block is the
pattern to copy:

```csharp
if (input.DataDogRumIdentifiedServices?.Length > 0)
{
    var tasks = new List<Task>();
    foreach (var pair in input.DataDogRumIdentifiedServices)
    {
        var parts = pair.Split('|');
        if (parts.Length == 2)
        {
            tasks.Add(context.CallActivityAsync(
                nameof(GetDataDogRumIdentifiedData),
                new DataDogRumIdentifiedInput(parts[0], parts[1], input.SkipStorage)));
            tasks.Add(context.CallActivityAsync(
                nameof(GetDataDogRumIdentifiedUsers),
                new DataDogRumIdentifiedInput(parts[0], parts[1], input.SkipStorage)));
        }
    }
    await Task.WhenAll(tasks);
}
```

These join the same fan-out as the existing DataDog activities, so all RUM
collection runs in parallel. The workflow class stays deterministic: no clock
reads, no HTTP, no `Task.Delay`.

`local-tests.http` gains the new field in the full request body and a narrow
request that exercises only this source.

## Secrets and CI changes

**No new secrets.** `DATADOGAPIKEY` and `DATADOGAPPKEY` already exist as
repository secrets, are already forwarded by `dapr.yaml`, and are the same keys
these activities use.

**One new env value in `run-workflow.yaml`**, alongside the existing `FIXED_*`
list:

```yaml
FIXED_DATADOG_IDENTIFIED_SERVICES: 'conductor-ui|conductor.r1.diagrid.io,conductor-ui|dapr-ops-dashboard.diagrid.io'
```

switched on by the **existing** `collect_datadog` checkbox:

```bash
DATADOG_ID_CSV=""    # initialised empty alongside the other *_CSV variables,
                     # so an unchecked box yields [] rather than an unset var
if [ "$COLLECT_DATADOG" = "true" ]; then
  DATADOG_CSV="$FIXED_DATADOG_RUM_SERVICES"
  DATADOG_ID_CSV="$FIXED_DATADOG_IDENTIFIED_SERVICES"
fi
```

and passed through the existing `csv_to_json` helper, which splits on `,` only —
verified — so a `service|env` entry survives intact. The new `--argjson` and the
`DataDogRumIdentifiedServices` key are added to the existing `jq -nc` payload
build.

**No new `workflow_dispatch` input.** The file is already at the cap of 10, and
reusing the `collect_datadog` checkbox keeps it there. Only the checkbox's
`description` changes, to mention identified users. The resolved value is
printed to the run log and run summary like every other list.

## Documentation changes

`AGENTS.md` requires four things to move together when a collector is added.
Three are covered above (`local-tests.http`, the `FIXED_*` value in
`run-workflow.yaml`, `postgres/postgres_schema.psql`); the fourth is the
`README.md` data-source list, which gains:

```
- Datadog RUM authenticated sessions and identified users for the `conductor-ui`
  service on `conductor.r1.diagrid.io` and `dapr-ops-dashboard.diagrid.io`
```

`AGENTS.md` itself gains two entries: a gotcha recording that `env` is a
hostname on `conductor-ui` rather than `prod` (the single most surprising fact
here), and a note under Database that `datadog_rum_identified_users` holds
customer PII and must never be logged or printed.

The `grafana/` export is left alone. It is already stale — it predates the
`datadog_rum` tables — and refreshing it is separate work.

## Privacy constraints

This feature puts roughly 65 real customer email addresses and names into a
database that has so far held only public metrics. Three constraints follow, and
the first is not negotiable.

**No email address or user name may ever be logged.** The repository is public,
so GitHub Actions run logs are world-readable, and the weekly collection runs
there. Both activities log counts only:

```
DataDog RUM identified conductor-ui@conductor.r1.diagrid.io week 2026-08-24: 210 sessions, 41 unique users
DataDog RUM identified users conductor-ui@conductor.r1.diagrid.io: 63 users over 3 weeks (12 with no install date)
```

No ids, no addresses, no names — not in `Console.WriteLine`, not in the run
summary, and not in an exception message, which means a failed request must not
echo a response body that could contain grouped email values. The existing
collectors already log counts only; here it is a requirement rather than a
coincidence, and it is the easiest thing in this design to break by accident.

**The view is the reporting surface.** Grafana panels read
`datadog_rum_identified_users_view`, which exposes counts. Selecting raw
addresses from the table is possible for whoever holds the connection string,
and that is the access boundary — the same one that already protects the
database.

**Erasure is only durable outside the lookback window.** A deletion request is
served by:

```sql
DELETE FROM datadog_rum_identified_users WHERE user_email = $1;
```

If that person is still active within the last three weeks, the next run re-adds
them with a fresh `install_date`. Making erasure permanent needs a deny-list the
collector consults before writing. That is a listed non-goal: documented so the
limitation is known, not built, because there is no such request today and a
deny-list would itself need to store the address it is suppressing.

There is no automatic expiry. Rows accumulate indefinitely, as every other table
here does.

## Error handling

Unchanged in shape from the existing DataDog collectors, which is the point of
mirroring them.

- **Per week, not per activity.** Each week's fetch and write is wrapped in its
  own `try`/`catch`; a failure logs and sets `allSucceeded = false`, and the
  remaining weeks still run. One bad week never discards two good ones.
- **Per `(service, env)` pair, independently.** Each pair is a separate activity
  call, so a failure on one host does not affect the other.
- **A non-2xx response throws** `HttpRequestException` carrying the status code
  and DataDog's error body, which contains an error message rather than result
  data and so cannot carry an email address. What must never be echoed is a
  body that parsed as *data*: a `FormatException` from `ParseUsers` reports what
  was structurally wrong, never the payload.
- **A missed week is cheap.** For the aggregate it is refetched next run while
  still inside retention. For the registry it costs precision on `install_date`
  for ids first seen that week, and nothing else, because the write never moves
  a recorded date.
- **Storage failure surfaces as a failed activity.** `PostgresOutput.InsertAsync`
  exceptions are not swallowed beyond the per-week catch.

## Testing

Following the repository's existing pattern: pure unit tests over parsing and
derivation, no network and no database. Activities and the workflow stay
untested, as they are today.

`DataDogRumResponse.Parse` is reused unchanged, so its existing tests carry the
aggregate path and nothing new is needed there.

**`ParseUsers`:**

- a well-formed response with all three facets present yields one `Observation`
  per bucket
- a bucket with `@usr.id` but no `@usr.email` key yields a null email, not a
  dropped observation
- a bucket with no `@usr.id` is skipped
- an empty `buckets` array yields an empty list
- an empty body throws `FormatException`
- malformed JSON throws `FormatException`
- a missing `data.buckets` array throws `FormatException`

**`DeriveEntries`:**

- an id seen in weeks 1 and 3 gets week 1 as its `install_date`
- an id seen only in the newest week gets that week
- an id first seen in the oldest week that returned data gets a null
  `install_date`
- leading weeks that returned no users are skipped rather than treated as the
  boundary
- the newest non-null email wins over an older one
- a null email in the newest week does not erase a known one from an earlier
  week
- the same rules hold independently for `user_name`

## Verification

Step 1 also answers the group-by question from the Architecture section, which
is why it comes first.

1. **Dry run.** Start the workflow from `local-tests.http` with only
   `DataDogRumIdentifiedServices` populated and `SkipStorage: true`. The
   aggregate activity's logged `unique users` and the registry activity's
   distinct-id count are both counts of distinct `@usr.id` in the same week, so
   **they must be equal**. A mismatch is the group-by drop behaviour, and the
   two-request fallback goes in before anything is stored.
2. **Sanity-check the magnitudes** against the Verified facts table: roughly 60+
   users and a few hundred authenticated sessions per week on
   `conductor.r1.diagrid.io`, single digits on `dapr-ops-dashboard.diagrid.io`.
3. **Dry-run the DDL** against the live schema with the aborted-transaction
   pattern — `DO $$ BEGIN <ddl>; RAISE EXCEPTION 'DRY RUN OK - rolling back';
   END $$;`. `P0001 DRY RUN OK` means every statement type-checks; any other
   SQLSTATE is a real failure.
4. **Apply the DDL.** Only after the maintainer approves it — this is the
   production database.
5. **Real run.** Expect 6 rows in `datadog_rum_identified` (2 pairs × 3 weeks)
   and roughly 66 rows in `datadog_rum_identified_users`.
6. **Re-run immediately.** This is the idempotency proof, and it is the check
   the RUM and Scarf features both used:
   - `datadog_rum_identified` has the same row count and the same
     `session_count` / `unique_user_count` values
   - `datadog_rum_identified_users` has the same row count, with every
     `install_date` and `collection_date` **unchanged**
7. **Check the view.** `total_users` is non-decreasing across weeks per
   `(service, env)`, the earliest week carries the seeded null-`install_date`
   users, and `total_external_users` is below `total_users` by roughly the 14
   internal accounts on the production host.
8. **Read the run log.** Confirm no email address, user name or user id appears
   anywhere in it, including in any error output.

## Known limitations

- **`install_date` means "first observed", at one-week resolution.** There is no
  way to recover a true signup date from RUM, and a user active before
  collection began gets a null that the view seeds into the first week on
  record. The first collected week therefore shows a large false acquisition
  spike.
- **`total_users` never decreases.** Churn is invisible. It counts accounts ever
  seen, not accounts still active.
- **The unauthenticated 78% is gone.** Each week's anonymous session count
  becomes unrecoverable 30 days after it happens. Changing this decision later
  cannot be backdated.
- **`env` is a hostname, so a redeploy to a new host silently starts a new
  series.** Nothing detects that; the symptom is a series that flatlines to zero
  while a new one appears. The `FIXED_DATADOG_IDENTIFIED_SERVICES` value is
  where it gets fixed.
- **An account signing into both surfaces counts twice** across the two
  `(service, env)` rows. Deliberate — the question is per-surface adoption — but
  it means the two `total_users` values must not be added together.
- **Erasure is not durable inside the lookback window.** See Privacy
  constraints.
- **Workflow state is in-memory.** A run interrupted halfway cannot be resumed
  and must be restarted from the beginning. Unchanged by this work, and harmless
  here because every write is idempotent.
