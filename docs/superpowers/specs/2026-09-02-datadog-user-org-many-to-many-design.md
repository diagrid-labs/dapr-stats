# DataDog identified-user ↔ organisation many-to-many — design

Date: 2026-09-02

## Problem

`datadog_rum_identified_users` stores one row per `(service, env, user_id)`
with a single `user_organization` column. That shape cannot represent the data
it is fed: `@usr.organization` is a per-**session** attribute, and a Conductor
user can belong to several organisations, so the relationship is
many-to-many.

`DataDogRumIdentifiedUsers.DeriveEntries` collapses the set to one value per
user — the newest non-null organisation observed — and every other
organisation that user was seen under is silently discarded. The write path
compounds nothing here; the loss happens before the SQL.

Measured consequence, reported by the GTM dashboard repo and reconfirmed
against live DataDog on 2026-09-02:
`count(distinct user_organization)` over `service = 'conductor-ui'` returns
**17** where DataDog has **24** for the same domains over the three weeks the
collector actually covered. The consumer cannot publish any organisation
figure, and a planned scorecard row was dropped.

The weekly aggregate table `datadog_rum_identified` is **not** affected. Every
stored row reconciles exactly with the DataDog RUM API, including `org_count`,
which is a genuine distinct count over real non-null organisation UUIDs. It is
verified correct and a GTM chart is built on it.

## Goal

One new table holding the full relationship, and the view's organisation
columns rederived from it.

**`datadog_rum_identified_user_orgs`** — one row per distinct
`(service, env, user_id, organization)`:

- `user_id` — the account id
- `organization` — one organisation that account was seen under
- `first_seen_week` — the Monday of the ISO week the *pair* was first seen in,
  where knowable
- `collection_date` — when the row was first recorded

**`datadog_rum_identified_users_view`** — `new_orgs` and `total_orgs` start
deriving from the link table instead of from the collapsed
`user_organization` column, so their values become correct. The view's column
list does not change.

Acceptance: over the collected window 2026-08-10 → 2026-08-31, both
production domains, the link table holds **64 rows covering 24 distinct
organisations** — 60 rows for `conductor.r1.diagrid.io` and 4 for
`dapr-ops-dashboard.diagrid.io` — matching DataDog's enumerated organisation
count and replacing today's 17.

Note the two numbers count different things, and the difference is the same
trap the consumer raised about the aggregate table. `env` is part of the key,
so a pair active on both domains is stored twice: there are 64 rows but only
**61 distinct `(user_id, organization)` pairs** across domains.
`count(distinct organization)` is safe to read across envs and gives 24;
`count(*)` is a per-env row count and must not be read as a pair count.

## Non-goals

- **Changing `datadog_rum_identified`.** No new columns, no changed week
  boundaries, no changed `@usr.id:*` basis. It is verified correct and a GTM
  chart depends on it. Explicitly requested by the consumer.
- **A combined-domain aggregate row.** The consumer's secondary ask: an extra
  row per `(service, week_start)` computed with both domains in one query, so
  a cross-domain total need not be summed (which over-counts anyone active on
  both domains during the migration — measured at 3 users and 2 organisations
  for the week of 2026-08-24). Deferred. It needs a decision about where the
  row lives: a sentinel `env` value in `datadog_rum_identified` would be
  picked up by the consumer's existing `SUM(...) GROUP BY week_start` and
  double their total silently, so the safe shapes are a separate table or a
  discriminator column. Not settled here.
- **Reworking `install_date`.** The consumer asked for it to be populated or
  its null documented. This design documents it (see *`install_date` and
  `first_seen_week` nulls*) and does not change how it is derived.
- **Backfilling the link table from `user_organization`.** Those 53 values
  carry no trustworthy first-seen week. See *Migration*.
- **Removing `user_organization`.** Kept and still written, so nothing
  existing breaks. See *Why the column stays*.

## Verified facts

All confirmed against the live DataDog RUM API on 2026-09-02, application
`Conductor UI`, query
`@type:session @session.type:user service:conductor-ui @usr.id:*` over
`env:conductor.r1.diagrid.io OR env:dapr-ops-dashboard.diagrid.io`. Not
guesses, and not taken on trust from the incoming report.

- **The organisation population is 29 over 2026-08-04 → 2026-09-02.**
  Grouping on `@usr.organization` alone, limit 1000, returns exactly 29
  buckets. The consumer's headline number is right.

- **Pair grouping is lossless.** Grouping on
  `[@usr.id, @usr.organization]` over the same window returns **72 buckets
  covering all 29 organisations** — every one of the 29, including all 8 for
  the busiest account. Nothing is dropped. This is the fact the whole design
  rests on: a per-user link table can reach an exact organisation count, so
  per-user attribution does not have to be traded away for correctness.

- **Over the collector's actual three-week lookback (2026-08-10 →
  2026-08-31) there are 61 pairs and 24 distinct organisations**, across 50
  distinct `@usr.id` values. This, not 29, is what one run can produce.

- **Split by deployment host, that is 60 pairs on
  `conductor.r1.diagrid.io` (23 organisations) and 4 on
  `dapr-ops-dashboard.diagrid.io` (3 organisations)** — 64 rows once `env` is
  part of the key, because three pairs are active on both hosts and one
  organisation appears only on `dapr-ops-dashboard`. The
  4 pairs on the second host reconcile with the report's 6 sessions / 3 users
  / 3 organisations for the week of 2026-08-24.

- **The multi-org population is one heavy account plus a short tail.** In the
  three-week window one `@usr.id` shows 8 organisations and four more show 2;
  the remaining 45 show 1. The single 8-organisation account accounts for most
  of the 24 → 17 shortfall.

- **Pair grouping drops users with no organisation.** A multi-facet
  `group_by` drops events missing any grouped facet — already documented in
  `GetDataDogRumIdentifiedUsers` and re-confirmed here. So the link table
  cannot double as the user registry; `datadog_rum_identified_users` remains
  the authoritative id spine. This is why the design adds a table rather than
  reshaping the existing one.

- **DataDog's `CARDINALITY` is approximate** (HyperLogLog): it reported 54
  distinct users where an enumerated `group_by` returned 50 buckets. Every
  number in this spec comes from an enumerated `group_by`, never from a
  cardinality estimate. Treat differences of 1–4 against `unique_user_count`
  or `org_count` as estimator noise, not defects.

## Architecture

An additive table plus a view rederivation. Three properties drove it:

1. **Nothing existing breaks.** `datadog_rum_identified` is untouched.
   `datadog_rum_identified_users` keeps its columns, its unique constraint and
   its grain. The view keeps its column list. The consumer's existing queries
   keep returning what they return today.
2. **The registry stays the id spine.** Because pair grouping drops
   organisation-less users, the link table is a strict subset of the registry
   by user id and cannot replace it.
3. **The fix lands before the SQL, not in it.** The loss is in
   `DeriveEntries`' collapse, so the change is a second derivation over the
   same fetched data — no extra DataDog requests beyond raising two limits.

### Why a link table and not a reshaped registry

The consumer offered one row per `(service, env, user_id, organization)` in
`datadog_rum_identified_users` itself. Rejected: it changes the grain under
existing consumers. `user_email` and `user_name` would repeat across a user's
rows, and the view's `new_users` would count the 8-organisation account eight
times unless every aggregate in the view were rewritten to `DISTINCT` — 57
rows becoming 72, with `total_users` silently inflating. A separate table
carries the many-to-many without touching anyone's existing grain.

### Why not an organisation-only dimension table

Also offered by the consumer: a distinct-organisation table populated straight
from session organisations, grouped on `@usr.organization` alone. It is the
simplest correct answer for counting, and it avoids the group-by product cap
entirely. Rejected because it drops per-user attribution, which is the
consumer's stated primary ask, and because the link table subsumes its
counting use — `count(distinct organization)` over the link table equals the
organisation-only enumeration, as verified above. Storing both would duplicate
the same organisation set in two places.

### Why the column stays

`user_organization` is exactly the column whose collapsed count produced this
report, so there is an argument for dropping it. It stays anyway: this
repository is public, the table is read by at least one consumer outside it,
and dropping a column turns their wrong-but-working query into an error in a
migration they did not schedule. Instead it keeps being written as the
newest-seen organisation and gains a schema comment saying it holds one of
possibly several and must not be counted — the warning goes where the next
person will read it. The consumer's own bug report is evidence a comment is
needed; it is not evidence the column must go.

### Where the truncation risk is

`GetDataDogRumIdentifiedUsers` sends the `orgs` request grouped by
`[@usr.id, @usr.organization]` with `AttributeGroupByLimit = 5` on the
organisation facet. DataDog's documented cap is on the **product** of every
facet's limit (10,000 groups across all dimensions), but the error message
does not say whether a second facet's limit is applied per parent bucket or as
a flat combination count, and the existing code comment already notes the fix
must hold under either reading.

Under the per-parent reading, a limit of 5 truncates the 8-organisation
account to 5 — so simply un-collapsing the derivation while leaving the limits
alone would reproduce a smaller version of the same bug. Raising the
organisation limit is therefore part of the fix, not a tuning detail.

## Components

### `DataDogRumIdentifiedUsers` (extended)

New constants for the `orgs` request only:

```
OrgUserGroupByLimit = 150   // vs 50 distinct users observed
OrgGroupByLimit     = 50    // vs 8 organisations on the busiest account
```

150 × 50 = 7,500, comfortably under the 10,000-group cap, and safe under
either reading of how that cap applies: the id limit covers 3× the observed
user count, and the organisation limit covers 6× the observed maximum per
user. `emails` and `names` keep `AttributeUserGroupByLimit = 500` and
`AttributeGroupByLimit = 5` — one email and one name per id is the norm, and
that product is already proven in production. The `orgs` request gets its own
pair of constants rather than sharing, because the two requests have
genuinely different fan-out.

New record:

```csharp
public sealed record OrgLink(
    string UserId, string Organization, DateOnly? FirstSeenWeek);
```

New method `DeriveOrgLinks`, mirroring `DeriveEntries`:

```csharp
public static IReadOnlyList<OrgLink> DeriveOrgLinks(
    IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Orgs)>
        weeksOldestFirst)
```

- One entry per distinct `(UserId, Organization)`.
- `FirstSeenWeek` is the earliest week the pair was observed in, or null when
  that is the boundary week — the oldest week that produced any pairs. Same
  convention as `DeriveEntries`, same reason: the boundary is the edge of what
  was queried, so the pair was probably active before it.
- Observations with a null `Organization` are skipped; there is no pair to
  record.
- An empty week is not the boundary, matching `DeriveEntries`.

`DeriveEntries` itself is **unchanged**. `user_organization` keeps its
newest-non-null-wins collapse, which is now a documented convenience rather
than the only record.

### `GetDataDogRumIdentifiedUsers` (extended)

`FetchWeekAsync` returns a three-part tuple instead of two:

```csharp
(IReadOnlyList<Observation> Users,
 IReadOnlyList<Observation> OrgPairs,
 bool AttributesDegraded)
```

`OrgPairs` is the `orgs` response passed through uncollapsed. The existing
`FirstNonNullByUserId(orgs, ...)` merge stays, feeding `user_organization` as
before — the same response now serves both purposes, so no extra request is
made. When the attribute block degrades, `OrgPairs` is empty and the
ids-only fallback is unchanged.

`RunAsync` accumulates a second per-week list, calls `DeriveOrgLinks` after
the week loop, and writes the pairs under the same
`if (!input.SkipStorage)` guard as the registry.

Logging gains the pair count and the distinct-organisation count. Counts only
— never an id, an email, a name or an organisation UUID.

### `StoreOrgAsync` (new)

```sql
insert into datadog_rum_identified_user_orgs
    (service, env, user_id, organization, first_seen_week, collection_date)
values ($1, $2, $3, $4, $5::date, $6)
on conflict (service, env, user_id, organization) do nothing
```

`do nothing`, not `do update`. A later run has a strictly shorter view of
history, so it must never move a recorded first date — the same reasoning that
keeps `install_date` out of the registry's update list. In particular a pair
whose `first_seen_week` was recorded as null at the boundary must not be
"corrected" by a later run, whose boundary is later and therefore worse.

There is no delete. The table accumulates, like the registry: a stale
organisation affiliation is never removed. That matches existing behaviour and
is what makes an all-time organisation count possible.

### Workflow, input, registration

No changes. `GetDataDogRumIdentifiedUsers` is an existing activity already
wired into `CollectorWorkflow` via the `service|env` pairs in
`DataDogRumIdentifiedServices`, and `AddDaprWorkflow()` discovers activities
without explicit registration. No new `CollectorWorkflowInput` field, so the
`workflow_dispatch` 10-input cap is not touched.

## Database schema

### `datadog_rum_identified_user_orgs`

```sql
-- One row per (user, organisation) pair ever observed. @usr.organization is a
-- per-SESSION attribute and a Conductor user can belong to several
-- organisations, so this relationship is many-to-many: as of 2026-09-02 one
-- account has been seen under 8 organisations and four more under 2. Count
-- organisations HERE, never with count(distinct user_organization) on
-- datadog_rum_identified_users -- that column keeps only one of them and
-- undercounts (17 against a true 24 when this table was added).
--
-- Holds a user id joined to an organisation identifier: PII, like
-- datadog_rum_identified_users. Never print or log these values -- this
-- repository is public and its GitHub Actions run logs are world-readable.
-- Report from datadog_rum_identified_users_view, which exposes counts.
--
-- first_seen_week is the Monday of the ISO week the PAIR was first observed
-- in, or NULL when that was the oldest week the run collected -- meaning the
-- pair existed at or before the edge of what was queried and its true first
-- week is unknowable. Same convention as install_date.
--
-- env is part of the key, so a pair active on both deployment hosts is stored
-- twice: count(*) is a row count, NOT a pair count. count(distinct
-- organization) is safe across envs; anything counting pairs needs
-- count(distinct (user_id, organization)).
CREATE TABLE datadog_rum_identified_user_orgs (
    id SERIAL PRIMARY KEY,
    service VARCHAR(255) NOT NULL,
    env VARCHAR(255) NOT NULL,
    user_id VARCHAR(128) NOT NULL,
    organization VARCHAR(128) NOT NULL,
    first_seen_week DATE,
    collection_date TIMESTAMP NOT NULL,
    CONSTRAINT datadog_rum_identified_user_orgs_unique
        UNIQUE (service, env, user_id, organization)
);
```

`organization` is `NOT NULL`: a null organisation is not a pair. Widths match
the registry's `user_id VARCHAR(128)` and `user_organization VARCHAR(128)`.

### `datadog_rum_identified_users.user_organization` (comment only)

A schema comment is added above the column; the column itself is unchanged:

```
-- user_organization holds ONE of possibly several organisations this user has
-- been seen under -- the most recently observed. It exists for labelling a
-- single user, and counting over it UNDERCOUNTS: it returned 17 distinct
-- values where DataDog had 24. Use datadog_rum_identified_user_orgs to count
-- organisations or to answer "which organisations does this user belong to".
```

### `datadog_rum_identified_users_view`

Column list unchanged, byte for byte. `CREATE OR REPLACE VIEW` can only
append columns, never insert, reorder or rename one, so leaving the `SELECT`
list alone is what keeps the next deployment from failing with 42P16. Two CTEs
change.

`org_first_week` derives from the link table:

```sql
org_first_week AS (
    SELECT o.service,
           o.env,
           o.organization,
           MIN(COALESCE(o.first_seen_week, f.week_start)) AS week_start
    FROM datadog_rum_identified_user_orgs o
    JOIN first_week f
      ON f.service = o.service AND f.env = o.env
    GROUP BY o.service, o.env, o.organization
)
```

The `COALESCE` seeds an unknown first week from the earliest week on record
for that `(service, env)`, exactly as `seeded` does for `install_date`, so
both halves of the view treat unknown history the same way. `MIN` over the
coalesced value keeps an organisation attributed to its earliest week even
when one of its pairs has a known week and another does not.

`week_spine` gains a third union arm:

```sql
week_spine AS (
    SELECT service, env, week_start FROM datadog_rum_identified
    UNION
    SELECT service, env, week_start FROM seeded
    UNION
    SELECT service, env, week_start FROM org_first_week
),
```

Same reason the existing comment gives for the second arm: the spine must not
be able to lose a row. A user can gain a new organisation in a week they were
not themselves first seen in, and if that week had no aggregate row the
`LEFT JOIN` from the spine would drop the organisation entirely and undercount
`total_orgs`. In practice the collected weeks cover it; the arm makes it
structural rather than incidental.

CTE order matters: `org_first_week` must be declared before `week_spine`
references it, which means moving it above `week_spine` in the `WITH` list.
`org_weeks` is otherwise unchanged — it already counts rows of
`org_first_week`.

### Effect on the view's numbers

`new_orgs` and `total_orgs` change value, upward, and do not change shape.
Nothing the consumer owns reads them; they confirmed this in writing. The
existing schema note explaining that `org_count` may exceed `total_orgs`
because the latter "collapses a user who changed organisation onto their
newest one" is now wrong and is rewritten: after this change `total_orgs`
counts every organisation attached to a registered user, and the remaining
reason the two can disagree is that `org_count` includes organisations whose
sessions carried an id the registry has not yet reached, plus cardinality
estimator noise.

## `install_date` and `first_seen_week` nulls

The consumer asked for `install_date` to be populated or its null documented.
It is documented, not changed — a null is meaningful and cannot be filled in
without inventing history.

A null `install_date` means the user was first observed in the **oldest week
the run collected**, so the account existed at or before the edge of the
three-week lookback and its true first week is outside RUM's 30-day retention.
It is unknowable, not uncaptured. This is why all three
`dapr-ops-dashboard.diagrid.io` rows are null: that domain's traffic begins
inside the lookback, so every one of its users was first seen in the boundary
week. `first_seen_week` on the new table carries the identical meaning for
pairs.

The distinction the consumer wants — "installed before we started collecting"
versus "not captured" — is therefore already available: null is always the
former. There is no "not captured" case, because a user with no observed week
is never written at all.

Two consequences worth stating plainly, both flagged to the consumer:

- The view resolves these nulls to `MIN(week_start)` of
  `datadog_rum_identified`, which is a floor, not a measurement. The more
  history that table holds, the further from the truth that floor sits.
- The consumer's planned backfill of 23 pre-August weeks
  (2026-02-27 → 2026-07-31, `conductor.r1.diagrid.io`) will relocate
  unknown-first-seen **organisations** to 2026-02-27 in `new_orgs`, exactly as
  it does users in `new_users`, because the link table inherits the same
  seeding. Not a regression from this change, but this change widens what the
  backfill moves. Separately: RUM retains raw session events for 30 days, so
  it is not clear pre-August session history is reachable from RUM at all —
  worth settling before that backfill runs.

Making first-seen stable under backfill (storing the boundary week explicitly
so the view stops seeding to `MIN(week_start)`) is a real improvement and is
deliberately out of scope here.

## Write strategy

Per `(service, env)`, per run:

1. Fetch three complete ISO weeks, oldest first — unchanged, and still shared
   with `GetDataDogRumIdentifiedData` through
   `DataDogRumIdentifiedUsers.WeeksPerRun` and `SearchQuery`, so the aggregate
   and both registries always describe the same population.
2. Derive registry entries (`DeriveEntries`, unchanged) and pairs
   (`DeriveOrgLinks`, new) from the same fetched weeks.
3. Upsert the registry — unchanged.
4. Insert pairs with `on conflict do nothing`.

Re-running a week inside retention is idempotent: the registry's upsert
corrects attributes, and the link table's `do nothing` adds only pairs it has
not seen. Neither can move a recorded first date.

## Error handling

Unchanged boundaries, which are already deliberately asymmetric:

- The `ids` request failing fails the whole week — without an id set there is
  nothing to register. Handled by the existing per-week `try`/`catch`.
- The attribute block failing must not discard the id set that already
  succeeded, so it falls back to ids-only observations with null attributes.
  Under this change that fallback also yields no pairs for the week, and the
  run reports degraded via the existing `allSucceeded = false`.
- A missing week costs precision on `first_seen_week` for pairs first seen
  that week and cannot corrupt a stored row, because the write never moves a
  first date.

Nothing new can fail: no new request, no new secret, no new component.

## Testing

TDD on `DeriveOrgLinks`, which is pure and sits exactly where this
repository's 117 tests already live — parsing and derivation helpers, no
network, no database. Added to `DataDogRumIdentifiedUsersTests`:

- A user seen under three organisations yields three entries.
- Two users sharing an organisation yield two entries.
- A pair first seen in the boundary week gets a null `FirstSeenWeek`.
- A pair first seen after the boundary week gets that week.
- A pair reappearing in a later week keeps the earlier week.
- An observation with a null organisation yields no entry.
- An empty oldest week is not the boundary; the next non-empty week is.
- No weeks yields no entries.
- A user whose organisation changes yields both, not the newest only — the
  regression test for this bug.

`DeriveEntries`' existing tests must keep passing untouched; that is the check
that `user_organization` behaviour is unchanged.

## Verification

1. `dotnet build dapr-stats.sln` and `dotnet test dapr-stats.sln` — all tests
   green, run time still around 100 ms.
2. Dry-run both DDL statements against the live schema inside an aborting
   block, expecting `P0001 DRY RUN OK`:

   ```sql
   DO $$
   BEGIN
     CREATE TABLE ... ;
     RAISE EXCEPTION 'DRY RUN OK - rolling back';
   END $$;
   ```

   The view replacement is dry-run the same way, which is also the 42P16
   check.
3. Run the collector with `SkipStorage = true` and confirm from the logs that
   `conductor.r1.diagrid.io` reports 60 pairs / 23 organisations and
   `dapr-ops-dashboard.diagrid.io` reports 4 pairs / 3 organisations, with no
   truncation warning on the `orgs` request. The activity runs once per
   `(service, env)`, so its logs are per-host — the combined 61-pair,
   24-organisation figure is not something any single run prints.
4. Apply the DDL — **after asking**, this is the production database — then
   run with `SkipStorage = false`.
5. Confirm against Neon:

   ```sql
   select env,
          count(*) rows_stored,
          count(distinct organization) orgs,
          count(distinct user_id) users,
          count(first_seen_week) has_first_seen
   from datadog_rum_identified_user_orgs
   where service = 'conductor-ui'
   group by env
   order by env;
   ```

   Expect 60 rows / 23 organisations for `conductor.r1.diagrid.io` and
   4 rows / 3 organisations for `dapr-ops-dashboard.diagrid.io`, and
   `count(distinct organization)` without the `group by` to be 24 — against
   the 17 the consumer measured.
6. Confirm `datadog_rum_identified_users_view` still returns its original
   column list in its original order, and that `total_orgs` has risen.
7. Confirm `datadog_rum_identified` is unchanged — no row changed, no column
   added.

## Migration

Two statements. Neon's HTTP endpoint takes one statement per request, so there
is no multi-statement transaction: `CREATE TABLE`, then
`CREATE OR REPLACE VIEW`. The `CREATE TABLE` is additive and cannot fail
partway in a way that affects existing data; if the view replacement fails,
the old view stays and the new table is simply unread.

**No backfill from `user_organization`.** Its 53 non-null values would seed 53
pairs whose first-seen week is unknown — and unlike a collected null, that
unknown is not "before the lookback" but "never recorded". Writing them would
bake a guess into the one column whose entire contract is *first seen*, and
`on conflict do nothing` would then prevent the next real run from correcting
it. The table starts empty and the collector fills it.

Consequence: `new_orgs` and `total_orgs` read 0 between the view replacement
and the first collector run. The run should follow in the same maintenance
window. The first run recovers the three-week lookback (24 organisations), not
the full 29 — the remainder accumulates as later runs see those organisations
again, and organisations last active before the lookback and never seen again
are unrecoverable, as they already are for users.

## Documentation changes

- `postgres/postgres_schema.psql` — the new table, the
  `user_organization` warning comment, the rewritten `total_orgs` note, the
  updated view.
- `AGENTS.md` — the new table named in the Database section's PII paragraph
  alongside `datadog_rum_identified_users`; the documented erasure `DELETE`
  extended to cover the link table's `user_id` rows; a Gotchas entry recording
  that `@usr.organization` is per-session and many-to-many, and that the
  per-facet group-by limit is what truncates it.
- `README.md` — the new table in the data-source list.
- `local-tests.http` — unchanged in shape, since no new input exists; the
  identified-services request is already the one that drives this.
- `run-workflow.yaml` — no new input needed, so no change beyond a comment if
  one is warranted.

## Known limitations

- **The first run reaches 24 organisations, not 29.** The five-day gap
  (2026-08-04 → 2026-08-09) is outside a three-complete-week lookback. The
  acceptance criterion as the consumer wrote it is not reachable by any single
  run, and stating that now is better than leaving a ticket open against an
  impossible number. The all-time count grows past 24 as runs accumulate.
- **Organisations attached only to organisation-less sessions are invisible
  here**, because pair grouping drops events missing a facet. Such an
  organisation would still appear in `datadog_rum_identified.org_count`, which
  is one legitimate reason the two can disagree.
- **Affiliations are never retired.** A user who leaves an organisation keeps
  the pair forever, so the table answers "has been seen under" and not "is
  currently a member of". Deliberate: it is what makes an all-time count
  possible, and RUM cannot support the other reading anyway.
- **The registry and the link table can disagree on user count.** The
  registry accumulates users seen before the current lookback; the link table
  only holds pairs. `count(distinct user_id)` over the link table is a lower
  bound on registered users, not a substitute for it.
- **The group-by limits are sized for today's volume**, with roughly 3× and 6×
  headroom. `PostAsync` already warns when a response reaches its limit, and
  that warning is the signal to raise them — but the `orgs` request's warning
  compares its bucket count (pairs) against the id-facet limit, which is a
  coverage heuristic rather than an exact truncation test.
- **An erasure request now touches two tables.** The documented deletion
  `DELETE FROM datadog_rum_identified_users WHERE user_email = $1` no longer
  removes everything about a user; the matching `user_id` rows in
  `datadog_rum_identified_user_orgs` must go too. Recorded in AGENTS.md.
