# DataDog identified-user ↔ organisation many-to-many Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store every `(user, organisation)` pair DataDog RUM reports instead of collapsing each user onto one organisation, so organisation counts stop undercounting (17 where DataDog has 24).

**Architecture:** Add `datadog_rum_identified_user_orgs`, a link table written by the existing `GetDataDogRumIdentifiedUsers` activity from the `orgs` grouped response it already fetches. The loss today is in `DeriveEntries`' collapse, not in the SQL, so the fix is a second pure derivation (`DeriveOrgLinks`) over the same fetched weeks plus a raised group-by limit. `datadog_rum_identified` and `datadog_rum_identified_users` keep their columns and grain; the view's `new_orgs` / `total_orgs` rederive from the link table.

**Tech Stack:** .NET 10, Dapr Workflow, xUnit, Neon Postgres 16 via the `daprstats` Dapr postgres binding, DataDog RUM Analytics Aggregate API v2.

**Spec:** [docs/superpowers/specs/2026-09-02-datadog-user-org-many-to-many-design.md](../specs/2026-09-02-datadog-user-org-many-to-many-design.md)

## Global Constraints

- **No git state-changing commands.** Do not run `git add`, `git commit`, `git push`, or any other mutating git command. This overrides the commit steps the plan-writing workflow would normally include. Each task ends at a **Checkpoint** step: report what changed and stop. The maintainer commits.
- **Never print or log a `@usr.id`, `@usr.email`, `@usr.name` or `@usr.organization` value.** This repository is public and its GitHub Actions run logs are world-readable. Counts only, always.
- **Do not change `datadog_rum_identified`** — not its columns, not its week boundaries, not its `@usr.id:*` basis. A GTM chart depends on it and it is verified correct.
- **Do not change `datadog_rum_identified_users_view`'s `SELECT` list** — not the columns, not their order. `CREATE OR REPLACE VIEW` can only append columns; reordering or renaming fails with `42P16`.
- **Do not change `DeriveEntries` or `user_organization` behaviour.** The existing `DeriveEntries` tests must keep passing untouched — that is the regression check.
- **Keep every DataDog `group_by` limit product under 10,000.** DataDog caps the *product* of every facet's limit at 10,000 groups across all dimensions, not 10,000 per facet.
- **Never apply DDL to the production database without asking first.** Neon project `spring-pine-41263944`, database `daprstats`. Dry-run first (Task 6).
- **SQL is hand-written with `$1`-style positional parameters** through `PostgresOutput.InsertAsync`. There is no EF Core and no direct Npgsql usage.
- **Guard every `InsertAsync` with `if (!input.SkipStorage)`.** This is what makes dry runs safe.
- Build: `dotnet build dapr-stats.sln`. Test: `dotnet test dapr-stats.sln`.

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `CollectDaprStats/DataDogRumIdentifiedUsers.cs` | Modify | Add the `OrgLink` record and the pure `DeriveOrgLinks` derivation. Parsing and derivation only — no I/O. |
| `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs` | Modify | Add a `DataDogRumIdentifiedUsersDeriveOrgLinksTests` class alongside the existing per-method test classes. |
| `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs` | Modify | Raise the `orgs` request limits, pass the `orgs` response through uncollapsed, derive pairs, log counts, write them. |
| `postgres/postgres_schema.psql` | Modify | The new table, the `user_organization` warning, the rewritten `total_orgs` note, the updated view. |
| `AGENTS.md` | Modify | PII paragraph, erasure `DELETE`, a Gotchas entry. |
| `README.md` | Modify | The new table in the data-source list. |

No new files, no new activity, no new workflow input, no `Program.cs` registration. `AddDaprWorkflow()` discovers activities without explicit registration, and `GetDataDogRumIdentifiedUsers` is already wired into `CollectorWorkflow` through the `service|env` pairs in `DataDogRumIdentifiedServices`.

---

### Task 1: `DeriveOrgLinks` — the pure derivation

This is where the bug is fixed. Everything else is plumbing.

**Files:**
- Modify: `CollectDaprStats/DataDogRumIdentifiedUsers.cs`
- Test: `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs`

**Interfaces:**
- Consumes: the existing `DataDogRumIdentifiedUsers.Observation(string UserId, string? Email, string? Name, string? Organization)` record — unchanged.
- Produces: `DataDogRumIdentifiedUsers.OrgLink(string UserId, string Organization, DateOnly? FirstSeenWeek)` and `static IReadOnlyList<OrgLink> DeriveOrgLinks(IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Orgs)> weeksOldestFirst)`. Task 2 calls both.

- [ ] **Step 1: Write the failing tests**

Append this class to `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs`. It mirrors the existing `DataDogRumIdentifiedUsersDeriveEntriesTests` conventions exactly — same `Week1`/`Week2`/`Week3` dates, same `Week(...)` and `User(...)` helpers, which are private to each test class and so must be repeated here.

```csharp
public class DataDogRumIdentifiedUsersDeriveOrgLinksTests
{
    private static readonly DateOnly Week1 = new(2026, 8, 10);
    private static readonly DateOnly Week2 = new(2026, 8, 17);
    private static readonly DateOnly Week3 = new(2026, 8, 24);

    private static (DateOnly, IReadOnlyList<DataDogRumIdentifiedUsers.Observation>) Week(
        DateOnly weekStart,
        params DataDogRumIdentifiedUsers.Observation[] users) => (weekStart, users);

    private static DataDogRumIdentifiedUsers.Observation User(
        string id, string? org = null) => new(id, null, null, org);

    // The regression test for the defect this change exists to fix. Before it,
    // DeriveEntries kept only the newest organisation and every other one was
    // silently discarded -- one production account emits 8.
    [Fact]
    public void DeriveOrgLinks_UserWithSeveralOrganizations_YieldsOnePairEach()
    {
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week2, User("u1", "org-1"), User("u1", "org-2")),
            Week(Week3, User("u1", "org-3")),
        ]);

        Assert.Equal(
            new[] { "org-1", "org-2", "org-3" },
            links.Where(l => l.UserId == "u1")
                 .Select(l => l.Organization)
                 .OrderBy(o => o)
                 .ToArray());
    }

    [Fact]
    public void DeriveOrgLinks_TwoUsersSharingAnOrganization_YieldsTwoPairs()
    {
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week2, User("u1", "org-1"), User("u2", "org-1")),
        ]);

        Assert.Equal(2, links.Count(l => l.Organization == "org-1"));
    }

    [Fact]
    public void DeriveOrgLinks_PairInOldestWeekWithData_GetsNullFirstSeenWeek()
    {
        // The oldest week that produced pairs is the edge of what was queried,
        // so the pair was probably active before it and its true first week is
        // unknowable. Same convention as install_date.
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("u1", "org-1")),
            Week(Week2, User("u1", "org-1")),
        ]);

        var link = Assert.Single(links);
        Assert.Null(link.FirstSeenWeek);
    }

    [Fact]
    public void DeriveOrgLinks_PairFirstSeenAfterTheBoundary_GetsThatWeek()
    {
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week3, User("u1", "org-1")),
        ]);

        var link = links.Single(l => l.UserId == "u1");
        Assert.Equal(Week3, link.FirstSeenWeek);
    }

    [Fact]
    public void DeriveOrgLinks_PairRepeatedInLaterWeeks_KeepsTheEarliestWeek()
    {
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week2, User("u1", "org-1")),
            Week(Week3, User("u1", "org-1")),
        ]);

        var link = links.Single(l => l.UserId == "u1");
        Assert.Equal(Week2, link.FirstSeenWeek);
    }

    [Fact]
    public void DeriveOrgLinks_RepeatedPairInOneWeek_YieldsOnePair()
    {
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week2, User("u1", "org-1"), User("u1", "org-1")),
        ]);

        Assert.Equal(1, links.Count(l => l.UserId == "u1"));
    }

    [Fact]
    public void DeriveOrgLinks_NullOrganization_YieldsNoPair()
    {
        // There is nothing to link. The user is still registered by
        // DeriveEntries, which works from the "ids" request.
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1, User("boundary", "org-b")),
            Week(Week2, User("u1")),
        ]);

        Assert.DoesNotContain(links, l => l.UserId == "u1");
    }

    [Fact]
    public void DeriveOrgLinks_LeadingWeeksWithoutPairsAreNotTheBoundary()
    {
        // A week that returned observations but no organisations is not the
        // boundary, exactly as an empty week is not.
        var links = DataDogRumIdentifiedUsers.DeriveOrgLinks(
        [
            Week(Week1),
            Week(Week2, User("noorg")),
            Week(Week3, User("u1", "org-1")),
        ]);

        var link = Assert.Single(links);
        Assert.Null(link.FirstSeenWeek);
    }

    [Fact]
    public void DeriveOrgLinks_NoWeeks_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumIdentifiedUsers.DeriveOrgLinks([]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter DeriveOrgLinks`
Expected: FAIL — compile errors, `'DataDogRumIdentifiedUsers' does not contain a definition for 'DeriveOrgLinks'` and for `OrgLink`.

- [ ] **Step 3: Add the `OrgLink` record**

In `CollectDaprStats/DataDogRumIdentifiedUsers.cs`, directly after the existing `Entry` record:

```csharp
        /// <summary>
        /// One row of the user-organisation link table. A user can hold
        /// several of these: <c>@usr.organization</c> is a per-SESSION
        /// attribute and a Conductor user can belong to more than one
        /// organisation, so the relationship is many-to-many.
        /// </summary>
        public sealed record OrgLink(
            string UserId, string Organization, DateOnly? FirstSeenWeek);
```

- [ ] **Step 4: Add `DeriveOrgLinks`**

In the same file, directly after `DeriveEntries`:

```csharp
        /// <summary>
        /// One entry per distinct (user, organisation) pair, with the start of
        /// the earliest ISO week the pair was observed in.
        /// </summary>
        /// <param name="weeksOldestFirst">
        /// Must be ordered oldest week first. Weeks with no pairs are allowed.
        /// </param>
        /// <remarks>
        /// The sibling of <see cref="DeriveEntries"/>, and deliberately not
        /// folded into it: that method answers "who is this user" and must keep
        /// yielding exactly one row per id, while this one answers "which
        /// organisations has this user been seen under" and yields as many rows
        /// as there are organisations. Collapsing the second question into the
        /// first is the defect this method exists to fix -- it undercounted
        /// organisations 17 against a true 24.
        /// <para>
        /// A pair first seen in the oldest week that produced any pairs gets a
        /// null first-seen week: that week is the edge of what was queried, so
        /// the pair probably existed before it and its true first week is
        /// unknowable. Same convention as install_date.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<OrgLink> DeriveOrgLinks(
            IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Orgs)> weeksOldestFirst)
        {
            var firstSeen = new Dictionary<(string UserId, string Organization), DateOnly>();
            DateOnly? boundary = null;

            foreach (var (weekStart, observations) in weeksOldestFirst)
            {
                var pairs = observations
                    .Where(o => o.Organization is not null)
                    .Select(o => (o.UserId, Organization: o.Organization!));

                var seenThisWeek = false;

                foreach (var pair in pairs)
                {
                    // The boundary is the oldest week that actually produced a
                    // pair. A week with no observations at all, or one whose
                    // observations all had a null organisation, is not it.
                    if (!seenThisWeek)
                    {
                        boundary ??= weekStart;
                        seenThisWeek = true;
                    }

                    firstSeen.TryAdd(pair, weekStart);
                }
            }

            return firstSeen
                .Select(kv => new OrgLink(
                    kv.Key.UserId,
                    kv.Key.Organization,
                    kv.Value == boundary ? null : kv.Value))
                .ToList();
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter DeriveOrgLinks`
Expected: PASS, 9 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test dapr-stats.sln`
Expected: PASS, 139 tests (130 existing + 9 new), still around 100 ms. Every existing `DeriveEntries` test must still pass — that is the check that `user_organization` behaviour is untouched.

- [ ] **Step 7: Checkpoint**

Report: files changed, test count before and after. Do not commit. Stop for review.

---

### Task 2: Fetch the pairs without collapsing them

**Files:**
- Modify: `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`

**Interfaces:**
- Consumes: `DataDogRumIdentifiedUsers.DeriveOrgLinks` and `DataDogRumIdentifiedUsers.OrgLink` from Task 1.
- Produces: an `orgLinks` list of `DataDogRumIdentifiedUsers.OrgLink` inside `RunAsync`, which Task 3 writes.

Note on placement: the spec's Components section describes the new limit constants under `DataDogRumIdentifiedUsers`. Put them in `GetDataDogRumIdentifiedUsers` instead, next to the existing `AttributeUserGroupByLimit` / `AttributeGroupByLimit`, which are `private const` there. They shape a request, not a derivation, and splitting the pair across two files would hide the 10,000-group arithmetic from the code that has to satisfy it.

- [ ] **Step 1: Add the `orgs` request limits**

In `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`, after the existing `AttributeGroupByLimit` declaration:

```csharp
        // The "orgs" request needs its own pair of limits, because it is the
        // one request whose second facet is genuinely multi-valued: one
        // production account has been seen under 8 organisations. Reusing
        // AttributeGroupByLimit (5) here would truncate that account to 5
        // under the per-parent reading of DataDog's cap -- a smaller version
        // of exactly the bug the link table exists to fix.
        //
        // 150 * 50 = 7,500, under the 10,000-groups cap and safe under either
        // reading of how that cap applies. Measured 2026-09-02 over the
        // three-week lookback: 50 distinct users on the busier host and 8
        // organisations on the busiest account, so this is ~3x and ~6x
        // headroom respectively.
        private const int OrgUserGroupByLimit = 150;
        private const int OrgGroupByLimit = 50;
```

- [ ] **Step 2: Point the `orgs` request at the new limits**

In `FetchWeekAsync`, replace the `orgs = await PostAsync(...)` call:

```csharp
                orgs = await PostAsync(
                    BuildGroupedBody(input, week,
                        [(DataDogRumIdentifiedUsers.UserIdFacet, OrgUserGroupByLimit),
                         (DataDogRumIdentifiedUsers.OrganizationFacet, OrgGroupByLimit)]),
                    "orgs", OrgUserGroupByLimit, week, apiKey, appKey);
```

Leave the `emails` and `names` calls exactly as they are: one email and one name per id is the norm, and their 500 × 5 product is already proven in production.

- [ ] **Step 3: Widen `FetchWeekAsync`'s return type**

Change the signature from the two-part tuple to three parts:

```csharp
        private async Task<(IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users,
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> OrgPairs,
            bool AttributesDegraded)> FetchWeekAsync(
                DataDogRumIdentifiedInput input, IsoWeek.Window week,
                string apiKey, string appKey)
```

In the `catch` block that handles a failed attribute request, return no pairs — the ids-only fallback is otherwise unchanged:

```csharp
                return (ids, [], true);
```

And at the end of the method, return the `orgs` response alongside the merged users. `orgs` now serves both purposes from one request: `FirstNonNullByUserId` still collapses it for `user_organization`, and the same list goes out uncollapsed for the link table.

```csharp
            return (users, orgs, false);
```

- [ ] **Step 4: Collect and derive the pairs in `RunAsync`**

Beside the existing `collected` declaration, add a second accumulator:

```csharp
            var collectedOrgs =
                new List<(DateOnly WeekStart,
                          IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Orgs)>();
```

Update the destructuring inside the per-week loop:

```csharp
                    var (users, orgPairs, attributesDegraded) =
                        await FetchWeekAsync(input, week, apiKey, appKey);
```

and add the pairs next to the existing `collected.Add(...)`:

```csharp
                    collected.Add((week.WeekStart, users));
                    collectedOrgs.Add((week.WeekStart, orgPairs));
```

After the loop, beside the existing `DeriveEntries` call:

```csharp
            var orgLinks = DataDogRumIdentifiedUsers.DeriveOrgLinks(collectedOrgs);
```

- [ ] **Step 5: Log the pair counts**

After the existing `Console.WriteLine` for entries, add:

```csharp
            var distinctOrgs = orgLinks
                .Select(l => l.Organization)
                .Distinct()
                .Count();
            var pairsWithoutFirstSeen = orgLinks.Count(l => l.FirstSeenWeek is null);

            // Counts only -- never an id or an organisation identifier. This is
            // the number to reconcile against DataDog: 60 pairs / 23 orgs for
            // conductor.r1.diagrid.io and 4 / 3 for
            // dapr-ops-dashboard.diagrid.io over the three-week lookback as of
            // 2026-09-02.
            Console.WriteLine(
                $"DataDog RUM identified user-orgs {input.Service}@{input.Env}: " +
                $"{orgLinks.Count} pairs over {distinctOrgs} distinct organisations " +
                $"({pairsWithoutFirstSeen} with no first-seen week)");
```

- [ ] **Step 6: Build and test**

Run: `dotnet build dapr-stats.sln`
Expected: no errors, no new warnings.

Run: `dotnet test dapr-stats.sln`
Expected: PASS, 139 tests. Nothing here is unit-tested — this activity has no test coverage, as none of the activities do — so the build is the check, and Task 6's dry run is the real verification.

- [ ] **Step 7: Checkpoint**

Report the diff. Confirm no `InsertAsync` was added yet, and that `emails` / `names` limits are unchanged. Do not commit. Stop for review.

---

### Task 3: Write the pairs

**Files:**
- Modify: `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`

**Interfaces:**
- Consumes: `orgLinks` from Task 2 and `DataDogRumIdentifiedUsers.OrgLink` from Task 1.
- Produces: rows in `datadog_rum_identified_user_orgs`, whose DDL is Task 4.

- [ ] **Step 1: Add `StoreOrgAsync`**

In `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`, directly after the existing `StoreAsync`:

```csharp
        private async Task StoreOrgAsync(
            DataDogRumIdentifiedInput input, DataDogRumIdentifiedUsers.OrgLink link)
        {
            const string tableName = "datadog_rum_identified_user_orgs";

            // `do nothing`, not `do update`. A later run has a strictly
            // shorter view of history, so it must never move a recorded first
            // date -- the same reasoning that keeps install_date out of the
            // registry's update list. In particular a pair recorded with a null
            // first_seen_week at the boundary must not be "corrected" by a
            // later run, whose boundary is later and therefore worse.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, user_id, organization, first_seen_week, " +
                " collection_date) " +
                "values ($1, $2, $3, $4, $5::date, $6) " +
                "on conflict (service, env, user_id, organization) do nothing";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                link.UserId,
                link.Organization,
                link.FirstSeenWeek?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }
```

A null `FirstSeenWeek` becomes a JSON `null` in the serialised `params` array and reaches Postgres as `NULL` — the same pattern the existing `StoreAsync` uses for `install_date`.

- [ ] **Step 2: Call it under the storage guard**

Extend the existing `if (!input.SkipStorage)` block so both writes sit under the one guard:

```csharp
            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input, entry);
                }

                foreach (var link in orgLinks)
                {
                    await StoreOrgAsync(input, link);
                }
            }
```

- [ ] **Step 3: Build and test**

Run: `dotnet build dapr-stats.sln`
Expected: no errors.

Run: `dotnet test dapr-stats.sln`
Expected: PASS, 139 tests.

- [ ] **Step 4: Checkpoint**

Report the diff. Confirm the write is inside `if (!input.SkipStorage)` and that the conflict clause is `do nothing`, not `do update`. Do not commit. Stop for review.

---

### Task 4: Schema and view

Code only — this task edits the checked-in schema file. Nothing is applied to the live database here; that is Task 6.

**Files:**
- Modify: `postgres/postgres_schema.psql`

**Interfaces:**
- Consumes: the table name, column names and unique-constraint columns used by `StoreOrgAsync` in Task 3 — `datadog_rum_identified_user_orgs(service, env, user_id, organization, first_seen_week, collection_date)`, unique on `(service, env, user_id, organization)`.
- Produces: the DDL text Task 6 dry-runs and applies.

- [ ] **Step 1: Add the new table**

In `postgres/postgres_schema.psql`, directly after the `CREATE TABLE datadog_rum_identified_users (...);` statement and before the `---` separator that precedes the view:

```sql
-- One row per (user, organisation) pair ever observed. @usr.organization is a
-- per-SESSION attribute and a Conductor user can belong to several
-- organisations, so this relationship is many-to-many: as of 2026-09-02 one
-- account has been seen under 8 organisations and four more under 2. Count
-- organisations HERE, never with count(distinct user_organization) on
-- datadog_rum_identified_users -- that column keeps only one of them and
-- undercounted 17 against a true 24 when this table was added.
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
-- organization) is safe across envs; counting pairs needs
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

- [ ] **Step 2: Warn on `user_organization`**

Replace the comment block above `CREATE TABLE datadog_rum_identified_users`. Find:

```
-- Holds real customer email addresses and names: the only PII in this database.
-- Never print or log these values -- this repository is public and its GitHub
-- Actions run logs are world-readable. Report from
-- datadog_rum_identified_users_view, which exposes counts.
```

Replace with:

```
-- Holds real customer email addresses and names: PII, along with
-- datadog_rum_identified_user_orgs. Never print or log these values -- this
-- repository is public and its GitHub Actions run logs are world-readable.
-- Report from datadog_rum_identified_users_view, which exposes counts.
--
-- user_organization holds ONE of possibly several organisations this user has
-- been seen under -- the most recently observed. It exists for labelling a
-- single user, and counting over it UNDERCOUNTS: it returned 17 distinct
-- values where DataDog had 24. Use datadog_rum_identified_user_orgs to count
-- organisations, or to answer "which organisations does this user belong to".
--
-- A NULL install_date means the user was first observed in the OLDEST week the
-- run collected, so the account existed at or before the edge of the
-- three-week lookback and its true first week is outside RUM's 30-day
-- retention. It is unknowable, not uncaptured -- there is no "not captured"
-- case, because a user with no observed week is never written at all. This is
-- why all three dapr-ops-dashboard.diagrid.io rows are null: that host's
-- traffic begins inside the lookback, so every one of its users was first seen
-- in the boundary week. datadog_rum_identified_user_orgs.first_seen_week means
-- the same thing for pairs.
--
-- datadog_rum_identified_users_view resolves these nulls to MIN(week_start) of
-- datadog_rum_identified, which is a FLOOR, not a measurement: the more
-- history that table holds, the further from the truth the floor sits. Anyone
-- backfilling earlier weeks into datadog_rum_identified moves every
-- null-install_date user and null-first_seen_week organisation to the new
-- earliest week in the view's new_users / new_orgs columns.
```

- [ ] **Step 3: Fix the now-wrong `total_orgs` note**

In the comment block above `CREATE TABLE datadog_rum_identified`, find:

```
-- org_count and the view's total_orgs count different populations and may
-- disagree in either direction. org_count is every distinct
-- @usr.organization seen in that week's sessions. total_orgs counts
-- organisations attached to REGISTERED users, so it omits an org whose users
-- all have a null user_organization, and it collapses a user who changed
-- organisation onto their newest one. A week where org_count exceeds
-- total_orgs is therefore expected, not a bug.
```

Replace with:

```
-- org_count and the view's total_orgs count different populations and may
-- disagree in either direction. org_count is every distinct
-- @usr.organization seen in that week's sessions. total_orgs counts
-- organisations recorded in datadog_rum_identified_user_orgs, which no longer
-- collapses a multi-organisation user -- so the remaining reasons the two
-- disagree are that org_count also counts organisations whose sessions carried
-- an id the registry has not reached yet, and that org_count is a cardinality
-- estimate. A week where org_count exceeds total_orgs is expected, not a bug.
```

- [ ] **Step 4: Rewrite the view's org CTEs**

Two changes to `CREATE OR REPLACE VIEW datadog_rum_identified_users_view`. The `SELECT` list is **not** touched.

First, move `org_first_week` above `week_spine` and point it at the link table. Delete the existing `org_first_week` CTE from its current position between `user_weeks` and `org_weeks`:

```sql
org_first_week AS (
    SELECT service, env, user_organization, MIN(week_start) AS week_start
    FROM seeded
    WHERE user_organization IS NOT NULL
    GROUP BY service, env, user_organization
),
```

and insert this in its place, immediately after the `seeded` CTE and before the `week_spine` comment block:

```sql
-- An organisation is new in the week its FIRST user appeared, not in every
-- week another of its users shows up. Counting distinct orgs per week would
-- count the same org again each time it gained a user.
--
-- Derived from datadog_rum_identified_user_orgs, not from
-- datadog_rum_identified_users.user_organization: that column keeps only one
-- of a user's organisations, which undercounted total_orgs 17 against a true
-- 24. The COALESCE seeds an unknown first week from the earliest week on
-- record, exactly as `seeded` does for install_date, so both halves of this
-- view treat unknown history the same way. MIN over the coalesced value keeps
-- an organisation on its earliest week even when one of its pairs has a known
-- week and another does not.
org_first_week AS (
    SELECT o.service,
           o.env,
           o.organization,
           MIN(COALESCE(o.first_seen_week, f.week_start)) AS week_start
    FROM datadog_rum_identified_user_orgs o
    JOIN first_week f
      ON f.service = o.service AND f.env = o.env
    GROUP BY o.service, o.env, o.organization
),
```

Second, add a third arm to `week_spine`. Find:

```sql
week_spine AS (
    SELECT service, env, week_start FROM datadog_rum_identified
    UNION
    SELECT service, env, week_start FROM seeded
),
```

Replace with:

```sql
week_spine AS (
    SELECT service, env, week_start FROM datadog_rum_identified
    UNION
    SELECT service, env, week_start FROM seeded
    UNION
    -- Third arm for the same reason as the second: the spine must not be able
    -- to lose a row. A user can gain an organisation in a week they were not
    -- themselves first seen in, and if that week had no aggregate row the LEFT
    -- JOIN below would drop the organisation and undercount total_orgs.
    SELECT service, env, week_start FROM org_first_week
),
```

`org_weeks` is unchanged — it already counts rows of `org_first_week`, and `COUNT(*)` over the new shape means the same thing.

- [ ] **Step 5: Verify the SELECT list is byte-identical**

Run: `git diff postgres/postgres_schema.psql`
Expected: the diff touches comments, the new `CREATE TABLE`, the `org_first_week` CTE and the `week_spine` CTE. It must show **no change** to any line between `SELECT s.service,` and `WINDOW w AS (PARTITION BY s.service, s.env ORDER BY s.week_start);`. If any of those lines changed, revert them — `CREATE OR REPLACE VIEW` cannot reorder or rename a column and will fail with `42P16`.

- [ ] **Step 6: Checkpoint**

Report the diff and confirm the `SELECT` list is untouched. Do not commit, and do not run anything against the live database yet. Stop for review.

---

### Task 5: Documentation

**Files:**
- Modify: `AGENTS.md`
- Modify: `README.md`

- [ ] **Step 1: Update the AGENTS.md Database section**

In the Database section, find:

```
`datadog_rum_identified_users` is the only table here holding PII: real customer
email addresses, names and organisation identifiers.
```

Replace with:

```
`datadog_rum_identified_users` and `datadog_rum_identified_user_orgs` are the
tables here holding PII: real customer email addresses, names and organisation
identifiers.
```

Then find the erasure sentence:

```
Deletion for an erasure request is
`DELETE FROM datadog_rum_identified_users WHERE user_email = $1`, but it only
sticks once that user falls outside the three-week lookback.
```

Replace with:

```
Deletion for an erasure request is two statements, because the link table keys
on `user_id` and not on the email:
`DELETE FROM datadog_rum_identified_user_orgs WHERE (service, env, user_id) IN
(SELECT service, env, user_id FROM datadog_rum_identified_users WHERE user_email = $1)`
first, then `DELETE FROM datadog_rum_identified_users WHERE user_email = $1`.
Doing it the other way round leaves the link rows unreachable. Either way it
only sticks once that user falls outside the three-week lookback.
```

- [ ] **Step 2: Add the Gotchas entry**

Append to the Gotchas list in `AGENTS.md`, after the `@usr.anonymous_id` entry:

```
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
```

- [ ] **Step 3: Update the README data-source list**

The list already mentions customer organisations, so this is one bullet reworded rather than a new entry. In `README.md`, find:

```
- Datadog RUM authenticated sessions, identified users and customer
  organisations for the `conductor-ui` service on `conductor.r1.diagrid.io` and
  `dapr-ops-dashboard.diagrid.io`, including running totals of unique users and
  unique organisations
```

Replace with:

```
- Datadog RUM authenticated sessions, identified users and customer
  organisations for the `conductor-ui` service on `conductor.r1.diagrid.io` and
  `dapr-ops-dashboard.diagrid.io`, including every organisation each user has
  been seen under and running totals of unique users and unique organisations
```

Leave the `collect_datadog` row in the inputs table alone — no new input exists.

- [ ] **Step 4: Checkpoint**

Report the diff. Do not commit. Stop for review.

---

### Task 6: Dry-run, apply, verify

This task touches the **production** database. Ask before applying anything.

**Files:** none — this is verification and migration.

**Interfaces:**
- Consumes: the DDL from Task 4 and the collector from Tasks 1–3.

- [ ] **Step 1: Get the connection string**

Run: `neon connection-string --project-id spring-pine-41263944 --role-name marcduiker --database-name daprstats`

The project has two roles, so `--role-name` is required. There is no `psql` on this machine and the Neon CLI has no SQL subcommand, so every query goes over the HTTP endpoint:

```
POST https://<endpoint-host>/sql
  -H "Neon-Connection-String: <conn string>"
  -H "Content-Type: application/json"
  -d '{"query": "...", "params": []}'
```

One statement per request, so no multi-statement transactions.

- [ ] **Step 2: Dry-run the CREATE TABLE**

Send the `CREATE TABLE datadog_rum_identified_user_orgs` statement from Task 4 wrapped so it cannot persist:

```sql
DO $$
BEGIN
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
  RAISE EXCEPTION 'DRY RUN OK - rolling back';
END $$;
```

Expected: SQLSTATE `P0001`, message `DRY RUN OK - rolling back`. The exception aborts the statement, so nothing persists. Any other SQLSTATE is a real failure — stop and report it.

- [ ] **Step 3: Dry-run the view replacement**

The view references the new table, which does not exist yet, so the two must be dry-run together in one `DO` block: the `CREATE TABLE` first, then the full `CREATE OR REPLACE VIEW` from Task 4, then the `RAISE`. This is also the `42P16` check — if the `SELECT` list drifted, this is where it surfaces.

Expected: SQLSTATE `P0001`, `DRY RUN OK - rolling back`. A `42P16` means a column was reordered, renamed or inserted in Task 4 — go back and fix it rather than working around it.

- [ ] **Step 4: Dry-run the collector**

Start the app and drive the identified-services request with `skipStorage: true`:

Run: `dapr run -f .`, then send the identified-services request from `local-tests.http` with `"skipStorage": true`.

Expected in the logs, for the three-week lookback as of 2026-09-02:

```
DataDog RUM identified user-orgs conductor-ui@conductor.r1.diagrid.io: 60 pairs over 23 distinct organisations (...)
DataDog RUM identified user-orgs conductor-ui@dapr-ops-dashboard.diagrid.io: 4 pairs over 3 distinct organisations (...)
```

and **no** `WARNING: "orgs" request ... at the group-by limit` line. Confirm no id, email, name or organisation UUID appears anywhere in the output.

These counts drift as weeks roll: the lookback moves every Monday. If the run happens later than 2026-09-02, re-derive the expected numbers by grouping RUM on `[@usr.id, @usr.organization]` per `env` over the three complete weeks the run actually covered, and compare against that rather than against 60 / 4.

- [ ] **Step 5: Ask before applying**

Stop. Report the dry-run results and ask the maintainer for explicit approval before applying DDL to production. Do not proceed without it.

- [ ] **Step 6: Apply the DDL**

Two requests, in order: `CREATE TABLE`, then `CREATE OR REPLACE VIEW`. The `CREATE TABLE` is additive and cannot affect existing data; if the view replacement fails, the old view stays and the new table is simply unread.

Note that `new_orgs` and `total_orgs` read 0 between the view replacement and the first real collector run, so Step 7 should follow immediately. There is deliberately no backfill from `user_organization` — its 53 values carry no trustworthy first-seen week, and `on conflict do nothing` would then stop a real run from ever correcting them.

- [ ] **Step 7: Run the collector for real**

Send the same request with `"skipStorage": false`.

Expected: the same pair counts as Step 4, and a successful activity completion.

- [ ] **Step 8: Verify the stored rows**

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

Expected: 60 rows / 23 organisations for `conductor.r1.diagrid.io`, 4 rows / 3 organisations for `dapr-ops-dashboard.diagrid.io`.

Then the acceptance criterion itself:

```sql
select count(distinct organization) from datadog_rum_identified_user_orgs
where service = 'conductor-ui';
```

Expected: **24**, against the 17 the consumer measured on `user_organization`. It is not 29 — the five days 2026-08-04 → 2026-08-09 fall outside a three-complete-week lookback, and no single run can reach them. The figure grows past 24 as runs accumulate.

- [ ] **Step 9: Verify nothing regressed**

```sql
select count(*) from datadog_rum_identified;
```

Expected: unchanged from before the migration — capture the count in Step 1 so there is something to compare against.

Then confirm the view still returns its original columns in their original order, and that `total_orgs` has risen:

```sql
select * from datadog_rum_identified_users_view
where service = 'conductor-ui'
order by env, week_start;
```

Expected: the same 12 columns in the same order — `service, env, week_start, new_users, new_external_users, total_users, total_external_users, new_orgs, total_orgs, session_count, unique_user_count, org_count` — with `total_orgs` higher than before and `new_users` / `total_users` unchanged.

- [ ] **Step 10: Checkpoint**

Report every count against its expected value, note any divergence rather than rounding it away, and confirm `datadog_rum_identified` is untouched. Do not commit. Stop for final review.

---

## Notes for the executor

- **`local-tests.http` and `run-workflow.yaml` need no changes.** No new workflow input exists — the collector reuses the `service|env` pairs already in `DataDogRumIdentifiedServices`, so the `workflow_dispatch` 10-input cap is not touched. The repo's "update all four" rule for a new collector does not apply, because this is not a new collector.
- **A full run is slow by design** (~25 minutes) because of the package retry loops. Driving only the identified-services request keeps this to seconds.
- **Workflow state is in-memory.** A run interrupted halfway cannot be resumed and must be started again from the beginning.
- **If the `orgs` truncation warning ever fires**, raise `OrgGroupByLimit` first and keep `OrgUserGroupByLimit * OrgGroupByLimit` under 10,000. Be aware that the warning compares the response's bucket count (pairs) against the id-facet limit, which is a coverage heuristic rather than an exact truncation test.
