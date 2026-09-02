# DataDog Identified-User Weekly Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **GIT POLICY — READ FIRST:** This repository's owner has a standing global
> instruction that no agent may run `git add`, `git commit`, `git push`, or any
> other state-changing git command unless explicitly asked for that operation in
> the current conversation. The commit steps below are written for the human
> engineer. **If you are an agent, stop at each commit step and ask the user to
> confirm before running it.** Read-only git commands (`status`, `diff`, `log`)
> are fine.

> **DATABASE POLICY:** Task 3 creates tables and a view in the live Neon
> database, and Task 8 writes rows to it. Do not run those without explicit
> confirmation in the current conversation, for the same reason as the git
> policy.

> **PRIVACY POLICY — THIS FEATURE IS DIFFERENT FROM EVERY OTHER COLLECTOR IN
> THIS REPO.** It handles real customer email addresses and names. This
> repository is public, so GitHub Actions run logs are world-readable. **Never
> print, log, echo, paste into a commit message, or write into a file any
> `@usr.id`, `@usr.email` or `@usr.name` value.** That includes tool output you
> paste back into a conversation. Test fixtures use invented addresses
> (`a@example.com`). When a verification step needs to look at real rows, it
> asks for counts, never for the values.

**Goal:** Two new Postgres tables and one view, fed from DataDog RUM as part of
the existing `CollectorWorkflow`, recording weekly authenticated sessions,
weekly unique identified users, and a running total of unique users for the
`conductor-ui` service on its two production hosts — so the history outlives
DataDog's 30-day event retention.

**Architecture:** Mirrors the existing `dev-dashboard` RUM pair. Two activities
per `(service, env)` pair, both fetching the same last three complete ISO weeks
via `IsoWeek.CompleteWeeksBefore`. `GetDataDogRumIdentifiedData` asks for a count
and a cardinality and upserts one `datadog_rum_identified` row per
`(service, env, week_start)`. `GetDataDogRumIdentifiedUsers` asks for the same
windows grouped by `@usr.id`, `@usr.email` and `@usr.name`, derives one entry per
distinct id (earliest week wins for `install_date`, newest non-null value wins
for the attributes) and upserts into `datadog_rum_identified_users` without ever
moving a recorded `install_date`. The running total is not collected: it is a
view over the registry. Both activities are idempotent by construction, so there
is no retry loop.

**Tech Stack:** .NET 10 (`net10.0`), Dapr.Workflow 1.18.5, Dapr.Client 1.18.5,
`System.Text.Json`, PostgreSQL via `bindings.postgresql`, xunit 2.9.2 for unit
tests.

**Spec:** `docs/superpowers/specs/2026-09-02-datadog-identified-user-collection-design.md`

## Global Constraints

- Target framework is `net10.0`. Do not retarget anything.
- `Nullable` and `ImplicitUsings` are `enable`. Match that in new files.
- The C# namespace is `DaprStats` (the csproj's `RootNamespace` says
  `WorkflowSample` — ignore it and follow the code).
- **Do not touch `Program.cs`.** Workflows and activities are never registered
  explicitly: `AddDaprWorkflow()` is called with no arguments and discovers
  `WorkflowActivity<,>` subclasses on its own. `IHttpClientFactory`,
  `PostgresOutput` and `DaprClient` are already registered.
- **Do not touch the existing `dev-dashboard` collectors.** `GetDataDogRumData`,
  `GetDataDogRumUsers`, `DataDogRumResponse`, `DataDogRumUsers`, `datadog_rum`
  and `datadog_rum_users` keep their current behaviour exactly. The only shared
  code this plan reuses is `IsoWeek` and `DataDogRumResponse`, both read-only.
- Existing code style: one workflow activity per file, file named after the
  activity class, `Console.WriteLine` for logging, records declared at the bottom
  of the activity's file, `const string tableName` inside the storage method.
- Dapr binding component name is `daprstats` (`resources/postgres.yml`). Secret
  store component name is `secretstore` (`resources/secrets.yml`).
- Secret keys, exact: `DATADOGAPIKEY` and `DATADOGAPPKEY`. RUM analytics needs
  **both**; the API key alone returns 403. No new secrets are needed.
- DataDog site is **US1**. Aggregate endpoint, exact:
  `https://api.datadoghq.com/api/v2/rum/analytics/aggregate`
- The DataDog search query, identical in both activities, exact:
  `@type:session @session.type:user service:{Service} env:{Env} @usr.id:*`
- Facet names, exact: `@usr.id`, `@usr.email`, `@usr.name`. The attributes
  `@usr.organization_id`, `@usr.org_id`, `@usr.role` and `@usr.tenant` **do not
  exist** on these events — do not query them.
- The two `(service, env)` pairs, exact:
  - `conductor-ui` / `conductor.r1.diagrid.io`
  - `conductor-ui` / `dapr-ops-dashboard.diagrid.io`
- `env` on `conductor-ui` is a **hostname**, not `prod`. An `env:prod` filter
  matches nothing on this service.
- Lookback is **3** complete ISO weeks for **both** activities, from the same
  `IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, 3)` call. Do not raise it to 4:
  four weeks reaches 34 days and exceeds RUM's 30-day retention, which returns a
  partial week that looks like a genuinely quiet week.
- `GroupByLimit` is **1000**, DataDog's default maximum for field grouping.
- Table names, exact: `datadog_rum_identified` and
  `datadog_rum_identified_users`. Constraint names, exact:
  `datadog_rum_identified_week_unique` and
  `datadog_rum_identified_users_unique`. View name, exact:
  `datadog_rum_identified_users_view`.
- The internal-account rule lives **only** in the view, as
  `user_email IS NULL OR user_email NOT LIKE '%@diagrid.io'`. No activity filters
  on an email domain.
- Every `InsertAsync` call sits behind `if (!input.SkipStorage)`.
- Workflow code may only await `WorkflowContext` members. No `DateTime.Now`,
  `Task.Delay`, `Task.Run`, `Guid.NewGuid`, or real I/O inside
  `CollectorWorkflow`. `DateTime.UtcNow` belongs in the activity.
- An empty or omitted `DataDogRumIdentifiedServices` array means "skip this
  source". The GitHub Actions checkboxes depend on it. Access it with
  `?.Length > 0`, matching `DataDogRumServices`, because the partial payloads in
  `local-tests.http` omit some arrays entirely.
- Run all tests with `dotnet test dapr-stats.sln`. The suite is 95 tests today
  and must stay green.
- Neon project id `spring-pine-41263944`, database `daprstats`, role
  `marcduiker`. There is no `psql` on this machine; SQL goes over Neon's HTTP
  endpoint (see Task 3).

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `CollectDaprStats/DataDogRumIdentifiedUsers.cs` | Create | Pure: parse one week's multi-facet grouped response, and derive install dates plus newest-known attributes across weeks |
| `CollectDaprStats/GetDataDogRumIdentifiedData.cs` | Create | Weekly counts activity: secrets, HTTP, logging, upsert. Owns `DataDogRumIdentifiedInput` |
| `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs` | Create | User-registry activity. Reuses `DataDogRumIdentifiedInput` |
| `CollectDaprStats/CollectorWorkflow.cs` | Modify | `DataDogRumIdentifiedServices` on the input record, plus the fan-out block calling both activities |
| `postgres/postgres_schema.psql` | Modify | The two table definitions and the view |
| `local-tests.http` | Modify | New payload field in both full payloads, plus an identified-users-only request |
| `.github/workflows/run-workflow.yaml` | Modify | New `FIXED_*` env value, the `DATADOG_ID_CSV` toggle, the new payload field, and the `collect_datadog` description |
| `README.md` | Modify | Data-source list entry |
| `AGENTS.md` | Modify | The `env`-is-a-hostname gotcha, and the PII note under Database |
| `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs` | Create | Tests for multi-facet parsing and the derivation rules |

`DataDogRumResponse.cs` is **reused unchanged** for the weekly aggregate — its
response shape (`c0` a count, `c1` a cardinality) is identical — so no new
parser and no new tests are needed on that path.

The pure logic is split out of the activities because an activity cannot be unit
tested without an HTTP harness this project does not have. Everything worth
testing lives in `DataDogRumIdentifiedUsers`, including all of the install-date
and attribute-precedence reasoning, which is the subtlest logic in the feature.
The activities are left as thin glue.

`DataDogRumIdentifiedUsers` is a **sibling** of the existing `DataDogRumUsers`,
not a generalisation of it. The existing class backs a collector that runs in
production every week; keeping them separate means this work cannot destabilise
`dev-dashboard` collection. The duplication is about 30 lines and is deliberate.

---

### Task 1: Confirm the multi-facet `group_by` response shape

This task writes no production code. Its deliverable is a recorded answer to the
one open question in the spec, plus two captured response shapes that Task 2's
tests are modelled on. Do it first — the answer decides whether the registry
activity makes one request per week or two.

**The question.** DataDog's documentation for RUM aggregation says events missing
a `group_by` field are dropped from the results. The comment in the existing
`CollectDaprStats/DataDogRumUsers.cs` says the opposite — that they arrive as a
bucket with the facet key absent (`"by": {}`), and `DataDogRumUsersParseIdsTests`
has a test asserting exactly that for a *single*-facet grouping. Both can be
true at once: single-facet and multi-facet grouping may behave differently. On
the production host there is one `@usr.id` with no `@usr.email`, so the answer
decides whether that user lands in the registry or vanishes from it.

**Files:**
- Create: nothing
- Modify: nothing

**Interfaces:**
- Consumes: nothing.
- Produces: a decision recorded in the conversation — **one request per week** or
  **two requests per week** — which Task 5 Step 1 reads. Also produces the
  captured JSON shapes used by Task 2's tests.

- [ ] **Step 1: Count the distinct ids with a single-facet grouping**

Use the DataDog MCP tools if available, otherwise `curl` with `DD-API-KEY` and
`DD-APPLICATION-KEY` headers against
`https://api.datadoghq.com/api/v2/rum/analytics/aggregate`.

Pick one complete ISO week inside retention — the Monday-to-Monday window used
below is an example; use a recent one. Request body:

```json
{
  "compute": [{ "aggregation": "count", "type": "total" }],
  "group_by": [{ "facet": "@usr.id", "limit": 1000 }],
  "filter": {
    "from": "2026-08-24T00:00:00Z",
    "to": "2026-08-31T00:00:00Z",
    "query": "@type:session @session.type:user service:conductor-ui env:conductor.r1.diagrid.io @usr.id:*"
  }
}
```

Record **only the number of buckets returned**. Do not print the ids.

- [ ] **Step 2: Count the distinct ids with the three-facet grouping**

Same window, same filter, three facets:

```json
{
  "compute": [{ "aggregation": "count", "type": "total" }],
  "group_by": [
    { "facet": "@usr.id", "limit": 1000 },
    { "facet": "@usr.email", "limit": 1000 },
    { "facet": "@usr.name", "limit": 1000 }
  ],
  "filter": {
    "from": "2026-08-24T00:00:00Z",
    "to": "2026-08-31T00:00:00Z",
    "query": "@type:session @session.type:user service:conductor-ui env:conductor.r1.diagrid.io @usr.id:*"
  }
}
```

Record the **number of distinct `@usr.id` values across the buckets** — not the
bucket count, since one id could in principle appear in more than one
combination.

- [ ] **Step 3: Record the decision**

- **Counts equal** → buckets keep the id when an attribute is missing.
  **Decision: one request per week.** Task 5 is implemented as written.
- **Three-facet count lower** → the three-facet request drops those events.
  **Decision: two requests per week.** Task 5 Step 1 adds the fallback described
  in its own note: a `@usr.id`-only request for the complete id set, plus the
  three-facet request for attributes, merged by id in the activity.

Write the decision and both numbers into the conversation so the Task 5
implementer can read it. This is the only place the answer is recorded.

- [ ] **Step 4: Capture the bucket shape for the tests**

From the three-facet response, note the **structure** of one bucket — which keys
appear under `by`, and whether a bucket exists with `@usr.id` present but
`@usr.email` absent. Write down the shape with the values replaced by
placeholders, for example:

```json
{
  "by": { "@usr.id": "<id>", "@usr.email": "<email>", "@usr.name": "<name>" },
  "computes": { "c0": 5 }
}
```

**Do not paste real ids, emails or names anywhere.** Task 2's fixtures use
`u1` / `a@example.com` / `Ada`.

- [ ] **Step 5: No commit**

Nothing changed on disk. Move to Task 2.

---

### Task 2: `DataDogRumIdentifiedUsers` parsing and derivation

**Files:**
- Create: `CollectDaprStats/DataDogRumIdentifiedUsers.cs`
- Test: `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs`

**Interfaces:**
- Consumes: the bucket shape confirmed in Task 1 Step 4.
- Produces, and Tasks 5 depends on these exact names:

```csharp
public static class DataDogRumIdentifiedUsers
{
    public sealed record Observation(string UserId, string? Email, string? Name);
    public sealed record Entry(
        string UserId, string? Email, string? Name, DateOnly? InstallDate);

    public static IReadOnlyList<Observation> ParseUsers(ReadOnlySpan<byte> json);

    public static IReadOnlyList<Entry> DeriveEntries(
        IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Users)> weeksOldestFirst);
}
```

- [ ] **Step 1: Write the failing parser tests**

Create `CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs`. Two test
classes in one file, matching the layout of the existing
`DataDogRumUsersTests.cs`:

```csharp
using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumIdentifiedUsersParseUsersTests
{
    private static IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Parse(string json) =>
        DataDogRumIdentifiedUsers.ParseUsers(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseUsers_ReturnsEveryBucket()
    {
        // Shape confirmed against the live API in Task 1: one bucket per
        // (id, email, name) combination, grouped by three facets.
        var users = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": "a@example.com", "@usr.name": "Ada" },
                    "computes": { "c0": 7 } },
                  { "by": { "@usr.id": "u2", "@usr.email": "b@example.com", "@usr.name": "Bo" },
                    "computes": { "c0": 2 } }
                ]
              }
            }
            """);

        Assert.Equal(
            new[]
            {
                new DataDogRumIdentifiedUsers.Observation("u1", "a@example.com", "Ada"),
                new DataDogRumIdentifiedUsers.Observation("u2", "b@example.com", "Bo"),
            },
            users);
    }

    [Fact]
    public void ParseUsers_BucketMissingEmailKey_YieldsNullEmail()
    {
        // One production id has no email. It must still be registered.
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.name": "Ada" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("u1", user.UserId);
        Assert.Null(user.Email);
        Assert.Equal("Ada", user.Name);
    }

    [Fact]
    public void ParseUsers_BucketMissingNameKey_YieldsNullName()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": "a@example.com" },
                    "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("a@example.com", user.Email);
        Assert.Null(user.Name);
    }

    [Fact]
    public void ParseUsers_BucketWithoutUserId_IsSkippedNotThrown()
    {
        // There is nothing to register without an id, and the query filters on
        // @usr.id:* anyway, so this is defensive.
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 1 } },
                  { "by": { "@usr.id": "u1" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Equal("u1", Assert.Single(users).UserId);
    }

    [Fact]
    public void ParseUsers_EmptyStringValues_AreTreatedAsMissing()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "", "@usr.email": "a@example.com" },
                    "computes": { "c0": 1 } },
                  { "by": { "@usr.id": "u1", "@usr.email": "", "@usr.name": "" },
                    "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("u1", user.UserId);
        Assert.Null(user.Email);
        Assert.Null(user.Name);
    }

    [Fact]
    public void ParseUsers_NonStringAttribute_IsTreatedAsMissing()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": null }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Null(Assert.Single(users).Email);
    }

    [Fact]
    public void ParseUsers_NoBuckets_ReturnsEmpty()
    {
        Assert.Empty(Parse("""{ "data": { "buckets": [] } }"""));
    }

    [Fact]
    public void ParseUsers_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumIdentifiedUsers.ParseUsers(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseUsers_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": {} }"""));
    }

    [Fact]
    public void ParseUsers_InvalidJson_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("not json"));
    }
}
```

- [ ] **Step 2: Write the failing derivation tests**

Append to the same file:

```csharp
public class DataDogRumIdentifiedUsersDeriveEntriesTests
{
    private static readonly DateOnly Week1 = new(2026, 8, 10);
    private static readonly DateOnly Week2 = new(2026, 8, 17);
    private static readonly DateOnly Week3 = new(2026, 8, 24);

    private static (DateOnly, IReadOnlyList<DataDogRumIdentifiedUsers.Observation>) Week(
        DateOnly weekStart,
        params DataDogRumIdentifiedUsers.Observation[] users) => (weekStart, users);

    private static DataDogRumIdentifiedUsers.Observation User(
        string id, string? email = null, string? name = null) =>
        new(id, email, name);

    [Fact]
    public void DeriveEntries_EarliestWeekWins()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1")),
            Week(Week3, User("u1")),
        ]);

        var u1 = entries.Single(e => e.UserId == "u1");
        Assert.Equal(Week2, u1.InstallDate);
    }

    [Fact]
    public void DeriveEntries_IdOnlyInNewestWeek_GetsThatWeek()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2),
            Week(Week3, User("u1")),
        ]);

        Assert.Equal(Week3, entries.Single(e => e.UserId == "u1").InstallDate);
    }

    [Fact]
    public void DeriveEntries_IdInOldestWeekWithData_GetsNullInstallDate()
    {
        // That week is the edge of what was queried, so the id was probably
        // active before it and its true first week is unknowable.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("u1")),
            Week(Week2, User("u2")),
        ]);

        Assert.Null(entries.Single(e => e.UserId == "u1").InstallDate);
        Assert.Equal(Week2, entries.Single(e => e.UserId == "u2").InstallDate);
    }

    [Fact]
    public void DeriveEntries_LeadingEmptyWeeksAreNotTheBoundary()
    {
        // The boundary is the oldest week that actually produced users. A week
        // that returned nothing (or failed) must not become it.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1),
            Week(Week2, User("u1")),
            Week(Week3, User("u2")),
        ]);

        Assert.Null(entries.Single(e => e.UserId == "u1").InstallDate);
        Assert.Equal(Week3, entries.Single(e => e.UserId == "u2").InstallDate);
    }

    [Fact]
    public void DeriveEntries_NewestNonNullEmailWins()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "old@example.com")),
            Week(Week3, User("u1", "new@example.com")),
        ]);

        Assert.Equal("new@example.com", entries.Single(e => e.UserId == "u1").Email);
    }

    [Fact]
    public void DeriveEntries_NullEmailInNewestWeek_DoesNotEraseKnownEmail()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "a@example.com")),
            Week(Week3, User("u1")),
        ]);

        Assert.Equal("a@example.com", entries.Single(e => e.UserId == "u1").Email);
    }

    [Fact]
    public void DeriveEntries_NameFollowsTheSameRule()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", name: "Ada")),
            Week(Week3, User("u1", name: null)),
        ]);

        Assert.Equal("Ada", entries.Single(e => e.UserId == "u1").Name);
    }

    [Fact]
    public void DeriveEntries_RepeatedIdInOneWeek_YieldsOneEntry()
    {
        // Multi-facet grouping can put the same id in two buckets if an
        // attribute differs between sessions.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "a@example.com"), User("u1", name: "Ada")),
        ]);

        var u1 = Assert.Single(entries.Where(e => e.UserId == "u1"));
        Assert.Equal("a@example.com", u1.Email);
        Assert.Equal("Ada", u1.Name);
    }

    [Fact]
    public void DeriveEntries_NoWeeks_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumIdentifiedUsers.DeriveEntries([]));
    }

    [Fact]
    public void DeriveEntries_AllWeeksEmpty_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1),
            Week(Week2),
        ]));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln`

Expected: the build fails with `CS0103`/`CS0246` — the name
`DataDogRumIdentifiedUsers` does not exist. A build failure is the correct
"failing test" state here; do not proceed until you see it.

- [ ] **Step 4: Write the implementation**

Create `CollectDaprStats/DataDogRumIdentifiedUsers.cs`:

```csharp
using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parsing and derivation for the identified-user registry.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="DataDogRumUsers"/> rather than a generalisation
    /// of it. That class backs the dev-dashboard collector, which runs weekly
    /// in production and has no identity attributes at all; keeping the two
    /// apart means changes here cannot affect it.
    /// </remarks>
    public static class DataDogRumIdentifiedUsers
    {
        private const string UserIdFacet = "@usr.id";
        private const string EmailFacet = "@usr.email";
        private const string NameFacet = "@usr.name";

        /// <summary>One bucket of one week's grouped response.</summary>
        public sealed record Observation(string UserId, string? Email, string? Name);

        /// <summary>One row of the registry.</summary>
        public sealed record Entry(
            string UserId, string? Email, string? Name, DateOnly? InstallDate);

        /// <summary>
        /// The users in one week's grouped aggregate response. Buckets with no
        /// id are skipped: there is nothing to register.
        /// </summary>
        public static IReadOnlyList<Observation> ParseUsers(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                throw new FormatException("DataDog returned an empty response body.");
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("DataDog response is not valid JSON.", ex);
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("DataDog response has no data.buckets array.");
            }

            var users = new List<Observation>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("by", out var by))
                {
                    continue;
                }

                var userId = ReadFacet(by, UserIdFacet);
                if (userId is null)
                {
                    continue;
                }

                users.Add(new Observation(
                    userId, ReadFacet(by, EmailFacet), ReadFacet(by, NameFacet)));
            }

            return users;
        }

        /// <summary>
        /// One entry per distinct id: the start of the earliest ISO week it was
        /// observed in, and the newest known email and name.
        /// </summary>
        /// <param name="weeksOldestFirst">
        /// Must be ordered oldest week first. Weeks with no users are allowed.
        /// </param>
        /// <remarks>
        /// An id first seen in the oldest week that returned any data gets a
        /// null install date: that week is the edge of what was queried, so the
        /// id was probably active before it and its true first week is
        /// unknowable. The view seeds those users into the earliest week on
        /// record.
        /// </remarks>
        public static IReadOnlyList<Entry> DeriveEntries(
            IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Users)> weeksOldestFirst)
        {
            var firstSeen = new Dictionary<string, DateOnly>();
            var attributes = new Dictionary<string, (string? Email, string? Name)>();
            DateOnly? boundary = null;

            foreach (var (weekStart, users) in weeksOldestFirst)
            {
                if (users.Count == 0)
                {
                    // An empty week is not the boundary -- the boundary is the
                    // oldest week that actually produced users.
                    continue;
                }

                boundary ??= weekStart;

                foreach (var user in users)
                {
                    firstSeen.TryAdd(user.UserId, weekStart);

                    // Oldest-first iteration plus null-coalescing means the
                    // newest non-null value wins, and a week where an attribute
                    // was absent cannot erase one that is known.
                    attributes.TryGetValue(user.UserId, out var known);
                    attributes[user.UserId] =
                        (user.Email ?? known.Email, user.Name ?? known.Name);
                }
            }

            return firstSeen
                .Select(kv => new Entry(
                    kv.Key,
                    attributes[kv.Key].Email,
                    attributes[kv.Key].Name,
                    kv.Value == boundary ? null : kv.Value))
                .ToList();
        }

        private static string? ReadFacet(JsonElement by, string facet)
        {
            if (!by.TryGetProperty(facet, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, with 20 more tests than before (95 → 115). If
`ParseUsers_ReturnsEveryBucket` fails on record equality, check that
`Observation` is a `record` — value equality is what the assertion relies on.

- [ ] **Step 6: Commit** *(agents: ask first — see the git policy at the top)*

```bash
git add CollectDaprStats/DataDogRumIdentifiedUsers.cs \
        CollectDaprStats.Tests/DataDogRumIdentifiedUsersTests.cs
git commit -m "feat: parse and derive DataDog RUM identified users"
```

---

### Task 3: Create the two tables and the running-total view

**Files:**
- Modify: `postgres/postgres_schema.psql`

**Interfaces:**
- Consumes: nothing.
- Produces: the `datadog_rum_identified` table with its
  `datadog_rum_identified_week_unique` constraint (Task 4's `on conflict`
  targets it), the `datadog_rum_identified_users` table with its
  `datadog_rum_identified_users_unique` constraint (Task 5's `on conflict`
  targets that one), and `datadog_rum_identified_users_view`.

> **This task writes to the live Neon database.** Get explicit confirmation
> before Step 4.

- [ ] **Step 1: Add the tables to the schema file**

In `postgres/postgres_schema.psql`, immediately after the existing
`CREATE TABLE datadog_rum_users (...)` block and before the `---` separator that
precedes the views, add:

```sql
-- unique_user_count is NOT additive: summing it across weeks double-counts
-- anyone active in more than one week. For a cumulative figure use
-- datadog_rum_identified_users_view.total_users.
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

Three notes to keep with the definitions, because each one is a decision rather
than an accident:

`session_count` is **authenticated** sessions only — sessions carrying an
`@usr.id`. On the production host that is about 22% of all sessions; the
unauthenticated remainder is deliberately not collected.

`env` is part of both unique constraints, which is what keeps the two production
hosts as independent series instead of merging them. The same account signing
into both surfaces therefore gets a separate `install_date` on each, and the two
`total_users` values must never be added together.

`user_email`, `user_name` and `install_date` are all nullable, each for its own
reason: at least one production id has no email or name, and `install_date` is
`NULL` when an id's first sighting is pinned to the retention floor.
`VARCHAR(320)` is the practical maximum email length (64-character local part,
`@`, 255-character domain). `VARCHAR(128)` on `user_id` is an assumption — the
ids were deliberately never printed during design, so their length is
unverified; 128 is generous for any UUID or opaque account id, and an over-long
value fails the insert loudly rather than truncating.

**This table holds customer PII.** It is the only table in this database that
does. Add that as a comment above `datadog_rum_identified_users`:

```sql
-- Holds real customer email addresses and names: the only PII in this database.
-- Never print or log these values -- this repository is public and its GitHub
-- Actions run logs are world-readable. Report from
-- datadog_rum_identified_users_view, which exposes counts.
```

- [ ] **Step 2: Add the view to the schema file**

After the two tables, following the file's `---` separator convention between
objects:

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

This is the running total, and it is the reporting surface — Grafana reads counts
from here rather than selecting raw addresses from the table. Four things about
it are deliberate:

- **Users with a `NULL` `install_date` are seeded into the earliest week on
  record**, not dropped. Dropping them would undercount the running total by the
  entire population that existed when collection began.
- **The internal/external split lives here**, not in the collector, so the
  `@diagrid.io` rule can change without a backfill and without having lost data.
- **A user with no email counts as external.** There is one such id today.
  Treating unknown as internal would understate customer numbers.
- **`total_users` is not a count of live accounts.** It is distinct accounts ever
  observed since collection began; churn is never subtracted.

The `INNER JOIN` means a user is invisible until the aggregate table has at least
one week for that `(service, env)`. Both activities run in the same workflow, so
that only shows during a partial first run.

- [ ] **Step 3: Dry-run the DDL against the live database**

There is no `psql` on this machine and the Neon CLI has no SQL subcommand, so SQL
goes through Neon's HTTP endpoint. Get the connection string with:

```bash
neon connection-string --project-id spring-pine-41263944 \
  --role-name marcduiker --database-name daprstats
```

The project has two roles, so `--role-name` is required. Then verify the two
`CREATE TABLE` statements compile and type-check against the live schema
**without changing anything**, by wrapping them in a `DO` block that raises at
the end. The exception aborts the statement, so nothing persists.

The SQL contains single quotes and `$$`, which are painful to nest inside a
shell-quoted `-d` argument. Write the request body to a file instead and post it
with `-d @file`. Build the file with `jq` so the SQL needs no manual escaping —
put the SQL in a heredoc, and let `jq -Rs` handle the JSON encoding:

```bash
SCRATCH="$TMPDIR/ddl-dryrun"   # or the session scratchpad directory
mkdir -p "$SCRATCH"

cat > "$SCRATCH/ddl.sql" <<'SQL'
DO $$
BEGIN
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
  RAISE EXCEPTION 'DRY RUN OK - rolling back';
END $$;
SQL

jq -Rs '{query: ., params: []}' < "$SCRATCH/ddl.sql" > "$SCRATCH/body.json"

curl -s -X POST "https://<endpoint-host>/sql" \
  -H "Neon-Connection-String: <conn string>" \
  -H "Content-Type: application/json" \
  -d @"$SCRATCH/body.json"
```

Expected: SQLSTATE `P0001` with message `DRY RUN OK - rolling back`. That means
both statements succeeded. **Any other SQLSTATE is a real failure** — fix it
before Step 4.

Neon's endpoint takes one statement per request, and a `DO` block counts as one,
which is why both tables fit in a single call. The **view cannot be validated
yet**: it reads tables that do not exist until Step 4. Validate it right after
Step 4, with the same `jq`/heredoc mechanism wrapping the
`CREATE OR REPLACE VIEW` in its own `DO` block.

- [ ] **Step 4: Apply the DDL**

**Stop and get explicit confirmation from the maintainer.** This is the
production database.

Then send the three statements as three separate requests, in order — the tables
first, the view last, since the view reads both tables. Use the same `curl`
shape without the `DO` wrapper.

- [ ] **Step 5: Verify the objects exist and are empty**

```sql
SELECT table_name, column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name IN ('datadog_rum_identified', 'datadog_rum_identified_users')
ORDER BY table_name, ordinal_position;
```

Expected: 7 columns for `datadog_rum_identified` and 8 for
`datadog_rum_identified_users`. Exactly three columns report `is_nullable = YES`
across both tables — `user_email`, `user_name` and `install_date`. Every other
column must be `NO`; a stray nullable column means a `NOT NULL` was dropped from
the statement.

```sql
SELECT count(*) FROM datadog_rum_identified;
SELECT count(*) FROM datadog_rum_identified_users;
SELECT count(*) FROM datadog_rum_identified_users_view;
```

Expected: `0`, `0`, `0`. The view returning zero rows rather than an error is the
check that its `JOIN` and window functions are valid.

- [ ] **Step 6: Commit** *(agents: ask first)*

```bash
git add postgres/postgres_schema.psql
git commit -m "feat: add datadog_rum_identified tables and running-total view"
```

---

### Task 4: `GetDataDogRumIdentifiedData` activity

**Files:**
- Create: `CollectDaprStats/GetDataDogRumIdentifiedData.cs`

**Interfaces:**
- Consumes: `IsoWeek.CompleteWeeksBefore(DateTime utcNow, int count)` returning
  `IReadOnlyList<IsoWeek.Window>` where `Window` is
  `(DateOnly WeekStart, DateTime From, DateTime To)`; and
  `DataDogRumResponse.Parse(ReadOnlySpan<byte>)` returning
  `(long SessionCount, long UniqueUserCount)`. Both exist already and are
  **reused unchanged**. Also consumes the `datadog_rum_identified` table from
  Task 3.
- Produces: the activity class `GetDataDogRumIdentifiedData`, and the record
  `DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage)` which
  Task 5 and Task 6 both use.

- [ ] **Step 1: Write the activity**

Create `CollectDaprStats/GetDataDogRumIdentifiedData.cs`. This is deliberately a
close copy of `GetDataDogRumData.cs` — read that file first. The differences are
the `env` filter, the `@usr.id:*` clause, the cardinality metric, the table, and
the three-column unique constraint.

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly authenticated-session and unique-user counts for a signed-in RUM
    /// service, per (service, env).
    /// </summary>
    public class GetDataDogRumIdentifiedData
        : WorkflowActivity<DataDogRumIdentifiedInput, bool>
    {
        // Complete ISO weeks fetched per run. Three is the largest lookback that
        // is always inside DataDog's 30-day RUM retention: at most 6 days of the
        // current partial week plus 21 days of complete weeks is 27 days. Four
        // weeks reaches 34 days, and a week only partly inside the retention
        // window returns a partial count that looks like a real low week.
        private const int WeeksPerRun = 3;

        // US1.
        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumIdentifiedData(
            IHttpClientFactory httpClientFactory,
            PostgresOutput output,
            DaprClient daprClient)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
            _daprClient = daprClient;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            DataDogRumIdentifiedInput input)
        {
            var apiKeySecrets = await _daprClient.GetSecretAsync(SecretStore, ApiKeySecret);
            var appKeySecrets = await _daprClient.GetSecretAsync(SecretStore, AppKeySecret);
            var apiKey = apiKeySecrets[ApiKeySecret];
            var appKey = appKeySecrets[AppKeySecret];

            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var allSucceeded = true;

            // Each week is independent: one failure must not discard the others.
            foreach (var week in weeks)
            {
                try
                {
                    var (sessionCount, uniqueUserCount) =
                        await FetchWeekAsync(input, week, apiKey, appKey);

                    // Counts only. Never log an id, email or name: this
                    // repository is public and its Actions logs are readable by
                    // anyone.
                    Console.WriteLine(
                        $"DataDog RUM identified {input.Service}@{input.Env} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {sessionCount} sessions, " +
                        $"{uniqueUserCount} unique users");

                    if (!input.SkipStorage)
                    {
                        await StoreAsync(input, week.WeekStart, sessionCount, uniqueUserCount);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM identified data for " +
                        $"{input.Service}@{input.Env} week {week.WeekStart:yyyy-MM-dd}: " +
                        ex.Message);
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<(long SessionCount, long UniqueUserCount)> FetchWeekAsync(
            DataDogRumIdentifiedInput input, IsoWeek.Window week,
            string apiKey, string appKey)
        {
            // @usr.id:* is what makes this authenticated sessions only -- 78% of
            // conductor-ui sessions have no identified user and are excluded.
            // @session.type:user excludes Synthetics traffic. env is a hostname
            // on this service, not `prod`.
            var searchQuery =
                $"@type:session @session.type:user service:{input.Service} " +
                $"env:{input.Env} @usr.id:*";

            var body = JsonSerializer.Serialize(new
            {
                compute = new object[]
                {
                    new { aggregation = "count", type = "total" },
                    new
                    {
                        aggregation = "cardinality",
                        type = "total",
                        metric = "@usr.id"
                    }
                },
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query = searchQuery
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, AggregateUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("DD-API-KEY", apiKey);
            request.Headers.Add("DD-APPLICATION-KEY", appKey);

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                // The error body carries DataDog's message, not result data, so
                // it cannot contain a user attribute.
                throw new HttpRequestException(
                    $"DataDog returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            // Same response shape as the dev-dashboard collector: c0 a count,
            // c1 a cardinality. The parser is reused as-is.
            return DataDogRumResponse.Parse(payload);
        }

        private async Task StoreAsync(
            DataDogRumIdentifiedInput input, DateOnly weekStart,
            long sessionCount, long uniqueUserCount)
        {
            const string tableName = "datadog_rum_identified";

            // The unique constraint is what makes the overlapping lookback safe:
            // a week re-collected inside retention is complete both times, so
            // overwriting corrects instead of duplicating.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, week_start, session_count, unique_user_count, collection_date) " +
                "values ($1, $2, $3::date, $4, $5, $6) " +
                "on conflict (service, env, week_start) do update " +
                "set session_count = excluded.session_count, " +
                "    unique_user_count = excluded.unique_user_count, " +
                "    collection_date = excluded.collection_date";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                sessionCount,
                uniqueUserCount,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public record DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build dapr-stats.sln`

Expected: succeeds with no warnings introduced. There is no unit test for this
class — an activity cannot be tested without an HTTP harness this project does
not have, and everything worth testing was extracted into pure helpers. It is
exercised by the dry run in Task 6.

- [ ] **Step 3: Run the existing tests**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, still 115 tests. This confirms nothing in the shared
`IsoWeek` / `DataDogRumResponse` path was disturbed.

- [ ] **Step 4: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/GetDataDogRumIdentifiedData.cs
git commit -m "feat: add DataDog RUM identified session and user count activity"
```

---

### Task 5: `GetDataDogRumIdentifiedUsers` activity

**Files:**
- Create: `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`

**Interfaces:**
- Consumes: `DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage)`
  from Task 4; `DataDogRumIdentifiedUsers.ParseUsers`,
  `DataDogRumIdentifiedUsers.DeriveEntries`,
  `DataDogRumIdentifiedUsers.Observation(string UserId, string? Email, string? Name)`
  and `DataDogRumIdentifiedUsers.Entry(string UserId, string? Email, string? Name, DateOnly? InstallDate)`
  from Task 2; `IsoWeek.CompleteWeeksBefore`; and the
  `datadog_rum_identified_users` table from Task 3.
- Produces: the activity class `GetDataDogRumIdentifiedUsers`, called by Task 6.

- [ ] **Step 1: Read Task 1's decision before writing anything**

Task 1 Step 3 recorded whether the three-facet `group_by` returns every
`@usr.id` or drops the ones missing an attribute.

- **One request per week** — implement Step 2 exactly as written.
- **Two requests per week** — implement Step 2, then apply the fallback in
  Step 3. Do not skip Step 3 in that case: the production user with no email
  would silently never be registered, and nothing downstream would report it.

- [ ] **Step 2: Write the activity**

Create `CollectDaprStats/GetDataDogRumIdentifiedUsers.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Maintains the registry of every identified user seen on a signed-in RUM
    /// service, per (service, env), with a first-seen week where knowable.
    /// </summary>
    public class GetDataDogRumIdentifiedUsers
        : WorkflowActivity<DataDogRumIdentifiedInput, bool>
    {
        // The same three complete ISO weeks GetDataDogRumIdentifiedData
        // collects, so install_date always lands on a Monday that has a
        // datadog_rum_identified row for the view to seed against.
        private const int WeeksPerRun = 3;

        // DataDog's default maximum for field grouping. Current volume is ~63
        // users per week on the busier host, so a single week is nowhere near
        // it.
        private const int GroupByLimit = 1000;

        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumIdentifiedUsers(
            IHttpClientFactory httpClientFactory,
            PostgresOutput output,
            DaprClient daprClient)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
            _daprClient = daprClient;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            DataDogRumIdentifiedInput input)
        {
            var apiKeySecrets = await _daprClient.GetSecretAsync(SecretStore, ApiKeySecret);
            var appKeySecrets = await _daprClient.GetSecretAsync(SecretStore, AppKeySecret);
            var apiKey = apiKeySecrets[ApiKeySecret];
            var appKey = appKeySecrets[AppKeySecret];

            // CompleteWeeksBefore returns oldest first, which is exactly the
            // order DeriveEntries needs.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var collected =
                new List<(DateOnly WeekStart,
                          IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users)>();
            var allSucceeded = true;

            foreach (var week in weeks)
            {
                try
                {
                    var users = await FetchWeekAsync(input, week, apiKey, appKey);

                    if (users.Count >= GroupByLimit)
                    {
                        Console.WriteLine(
                            $"WARNING: week {week.WeekStart:yyyy-MM-dd} returned " +
                            $"{users.Count} users, at the group-by limit of " +
                            $"{GroupByLimit}. Users are being dropped.");
                    }

                    collected.Add((week.WeekStart, users));
                }
                catch (Exception ex)
                {
                    // A missing week costs precision on install_date for users
                    // first seen that week, and the next run refetches it
                    // anyway. It cannot corrupt a stored row, because the write
                    // never moves install_date.
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM identified users for " +
                        $"{input.Service}@{input.Env} week {week.WeekStart:yyyy-MM-dd}: " +
                        ex.Message);
                    allSucceeded = false;
                }
            }

            var entries = DataDogRumIdentifiedUsers.DeriveEntries(collected);

            var withoutInstallDate = entries.Count(e => e.InstallDate is null);
            var withoutEmail = entries.Count(e => e.Email is null);

            // Counts only -- never an id, email or name.
            Console.WriteLine(
                $"DataDog RUM identified users {input.Service}@{input.Env}: " +
                $"{entries.Count} distinct users over {collected.Count} weeks " +
                $"({withoutInstallDate} with no install date, " +
                $"{withoutEmail} with no email)");

            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input, entry);
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<DataDogRumIdentifiedUsers.Observation>>
            FetchWeekAsync(
                DataDogRumIdentifiedInput input, IsoWeek.Window week,
                string apiKey, string appKey)
        {
            var body = JsonSerializer.Serialize(new
            {
                compute = new object[] { new { aggregation = "count", type = "total" } },
                group_by = new object[]
                {
                    new { facet = "@usr.id", limit = GroupByLimit },
                    new { facet = "@usr.email", limit = GroupByLimit },
                    new { facet = "@usr.name", limit = GroupByLimit }
                },
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query =
                        $"@type:session @session.type:user service:{input.Service} " +
                        $"env:{input.Env} @usr.id:*"
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, AggregateUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("DD-API-KEY", apiKey);
            request.Headers.Add("DD-APPLICATION-KEY", appKey);

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"DataDog returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return DataDogRumIdentifiedUsers.ParseUsers(payload);
        }

        private async Task StoreAsync(
            DataDogRumIdentifiedInput input, DataDogRumIdentifiedUsers.Entry entry)
        {
            const string tableName = "datadog_rum_identified_users";

            // `do update` on the attributes only. install_date and
            // collection_date are absent from the update list on purpose: a
            // later run has a strictly shorter view of history, so it must never
            // overwrite a recorded first date. The coalesce stops a week where
            // an attribute was absent from erasing a known value.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, user_id, user_email, user_name, install_date, collection_date) " +
                "values ($1, $2, $3, $4, $5, $6::date, $7) " +
                "on conflict (service, env, user_id) do update " +
                $"set user_email = coalesce(excluded.user_email, {tableName}.user_email), " +
                $"    user_name = coalesce(excluded.user_name, {tableName}.user_name)";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                entry.UserId,
                entry.Email!,
                entry.Name!,
                entry.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
```

Two details in `StoreAsync` that look wrong and are not. The `!` on
`entry.Email` and `entry.Name` suppresses a nullable warning while still putting
a real `null` into the `object[]`; this matches how `GetDataDogRumUsers` already
passes a nullable `install_date`, and `PostgresOutput.InsertAsync` serialises the
array to JSON, where `null` becomes SQL `NULL`. And the table name is
interpolated into the `coalesce` because Postgres requires the target table
qualified there — `excluded` is the incoming row, the bare table name is the
existing one.

- [ ] **Step 3: Apply the two-request fallback — only if Task 1 said so**

Skip this step entirely if Task 1's decision was "one request per week".

If the three-facet request drops ids that are missing an attribute, replace
`FetchWeekAsync` with a version that makes two calls and merges them. Add a
private helper that posts a body and returns the parsed users, then:

```csharp
private async Task<IReadOnlyList<DataDogRumIdentifiedUsers.Observation>>
    FetchWeekAsync(
        DataDogRumIdentifiedInput input, IsoWeek.Window week,
        string apiKey, string appKey)
{
    // Two requests, because a multi-facet group_by drops events that are
    // missing one of the facets -- confirmed against the live API. The
    // id-only request is the authoritative set of users; the three-facet
    // request only supplies attributes.
    var ids = await PostAsync(
        BuildBody(input, week, ["@usr.id"]), apiKey, appKey);
    var attributed = await PostAsync(
        BuildBody(input, week, ["@usr.id", "@usr.email", "@usr.name"]),
        apiKey, appKey);

    var byId = attributed
        .GroupBy(u => u.UserId)
        .ToDictionary(
            g => g.Key,
            g => (Email: g.Select(u => u.Email).FirstOrDefault(e => e is not null),
                  Name: g.Select(u => u.Name).FirstOrDefault(n => n is not null)));

    return ids
        .Select(u => byId.TryGetValue(u.UserId, out var attrs)
            ? new DataDogRumIdentifiedUsers.Observation(u.UserId, attrs.Email, attrs.Name)
            : u)
        .ToList();
}
```

`BuildBody` is the existing serialisation with the facet list parameterised, and
`PostAsync` is the existing request-and-parse block. The merge is a left join on
the id-only set, so a user with no attributes keeps nulls rather than
disappearing.

- [ ] **Step 4: Build and run the tests**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`

Expected: build succeeds, 115 tests pass.

- [ ] **Step 5: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/GetDataDogRumIdentifiedUsers.cs
git commit -m "feat: register DataDog RUM identified users per service and env"
```

---

### Task 6: Wire both activities into `CollectorWorkflow`

**Files:**
- Modify: `CollectDaprStats/CollectorWorkflow.cs`
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: `GetDataDogRumIdentifiedData` and `GetDataDogRumIdentifiedUsers` from
  Tasks 4 and 5, and `DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage)`.
- Produces: the `DataDogRumIdentifiedServices` field on
  `CollectorWorkflowInput`, which Task 7 populates from CI.

- [ ] **Step 1: Add the field to `CollectorWorkflowInput`**

In `CollectDaprStats/CollectorWorkflow.cs`, in the `CollectorWorkflowInput`
record at the bottom of the file, add `DataDogRumIdentifiedServices` immediately
after `DataDogRumServices`:

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
        string[] DataDogRumIdentifiedServices,
        string[] ScarfBuildingBlockPixelIds,
        string[] ScarfLeaderboardPixelIds,
        bool SkipStorage);
```

- [ ] **Step 2: Add the fan-out block**

In the same file, immediately after the existing
`if (input.DataDogRumServices?.Length > 0) { ... }` block, add:

```csharp
            if (input.DataDogRumIdentifiedServices?.Length > 0)
            {
                var getDataDogRumIdentifiedTasks = new List<Task>();
                foreach (var pair in input.DataDogRumIdentifiedServices)
                {
                    // "service|env". env is a hostname on conductor-ui, not
                    // `prod`, so it cannot be derived and has to be given.
                    var parts = pair.Split('|');
                    if (parts.Length == 2)
                    {
                        getDataDogRumIdentifiedTasks.Add(context.CallActivityAsync(
                            nameof(GetDataDogRumIdentifiedData),
                            new DataDogRumIdentifiedInput(
                                parts[0], parts[1], input.SkipStorage)));
                        getDataDogRumIdentifiedTasks.Add(context.CallActivityAsync(
                            nameof(GetDataDogRumIdentifiedUsers),
                            new DataDogRumIdentifiedInput(
                                parts[0], parts[1], input.SkipStorage)));
                    }
                }
                await Task.WhenAll(getDataDogRumIdentifiedTasks);
            }
```

`?.Length > 0` rather than `.Length > 0` because the partial payloads in
`local-tests.http` omit some arrays entirely, and an omitted JSON field
deserialises to `null`. A malformed entry is skipped rather than throwing, which
matches how `DockerHubImages` handles a name without a `/`.

- [ ] **Step 3: Add the field to the two full payloads in `local-tests.http`**

In the "Start a complete CollectorWorkflow" body and the "Start a partial
CollectorWorkflow" body, add the field after `"DataDogRumServices"`. The complete
one gets both pairs:

```json
    "DataDogRumServices" : ["dev-dashboard"],
    "DataDogRumIdentifiedServices" : ["conductor-ui|conductor.r1.diagrid.io", "conductor-ui|dapr-ops-dashboard.diagrid.io"],
```

and the partial one gets an empty array, matching its `"DataDogRumServices" : []`:

```json
    "DataDogRumIdentifiedServices" : [],
```

Also add `"DataDogRumIdentifiedServices" : []` to the existing "DataDog RUM only"
request, so that one keeps collecting only `dev-dashboard`.

- [ ] **Step 4: Add an identified-users-only request to `local-tests.http`**

After the existing "DataDog RUM only, no storage" block, add:

```
###
### DataDog RUM identified users only, no storage. Logs three complete ISO
### weeks for both conductor-ui hosts and writes nothing. Completes in seconds.
###
### Check the log for two things:
###  1. the aggregate activity's "unique users" and the registry activity's
###     "distinct users" must be EQUAL for the same host -- both are counts of
###     distinct @usr.id in the same weeks. A mismatch means the multi-facet
###     group_by is dropping users (see the plan's Task 1).
###  2. no email address, user name or user id anywhere in the output.
###
POST {{dapr_url}}/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
Content-Type: application/json

{
    "CollectionDate" : "{{currentDate}}",
    "NuGetPackageNames" : [],
    "NpmPackageNames" : [],
    "PythonPackageNames" : [],
    "CollectDiscordData" : false,
    "CollectGitHubData" : false,
    "CollectDiagridDashboardData" : false,
    "DockerHubImages" : [],
    "DataDogRumServices" : [],
    "DataDogRumIdentifiedServices" : ["conductor-ui|conductor.r1.diagrid.io", "conductor-ui|dapr-ops-dashboard.diagrid.io"],
    "SkipStorage": true
}

###
```

- [ ] **Step 5: Build and run the tests**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`

Expected: build succeeds, 115 tests pass. The workflow has no unit tests, so this
only proves it compiles — the dry run in Task 8 is what exercises it.

- [ ] **Step 6: Dry-run it locally**

```bash
dapr run -f .
```

Then send the new "DataDog RUM identified users only, no storage" request from
`local-tests.http`. It completes in seconds and writes nothing.

Verify all three things the request's comment lists: six week lines total (three
per host), the aggregate and registry user counts agreeing per host, and no
`@usr.*` value in the output. Magnitudes to expect, from the spec's Verified
facts: roughly 60+ users and a few hundred authenticated sessions per week on
`conductor.r1.diagrid.io`, single digits on `dapr-ops-dashboard.diagrid.io`.

If the two counts disagree, stop: go back to Task 5 Step 3 and apply the
two-request fallback.

- [ ] **Step 7: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/CollectorWorkflow.cs local-tests.http
git commit -m "feat: call DataDog RUM identified collection from CollectorWorkflow"
```

---

### Task 7: CI wiring and documentation

**Files:**
- Modify: `.github/workflows/run-workflow.yaml`
- Modify: `README.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: the `DataDogRumIdentifiedServices` payload field from Task 6.
- Produces: nothing code depends on.

- [ ] **Step 1: Add the fixed list**

In `.github/workflows/run-workflow.yaml`, in the `env:` block, after
`FIXED_DATADOG_RUM_SERVICES`:

```yaml
  FIXED_DATADOG_IDENTIFIED_SERVICES: 'conductor-ui|conductor.r1.diagrid.io,conductor-ui|dapr-ops-dashboard.diagrid.io'
```

The entries are `service|env`, comma-separated between pairs. `csv_to_json`
splits on `,` only — verified — so the `|` passes through intact.

**No new `workflow_dispatch` input.** `workflow_dispatch` allows at most 10
inputs and this file is exactly at the cap. This list rides the existing
`collect_datadog` checkbox instead.

- [ ] **Step 2: Update the checkbox description**

In the `workflow_dispatch.inputs` block:

```yaml
      collect_datadog:
        description: 'Collect Datadog RUM views, users and identified users'
        required: false
        type: boolean
        default: true
```

- [ ] **Step 3: Wire the toggle**

In the "Generate Variables and Start CollectorWorkflow" step, add
`DATADOG_ID_CSV=""` to the group of blanked lists and set it inside the existing
`collect_datadog` branch:

```bash
          # CollectorWorkflow skips any collector whose list is empty, so the
          # three toggles are applied by blanking out the fixed lists.
          DOCKERHUB_CSV=""
          DATADOG_CSV=""
          DATADOG_ID_CSV=""
          SCARF_BB_CSV=""
          SCARF_LB_CSV=""
          if [ "$COLLECT_DOCKERHUB" = "true" ]; then
            DOCKERHUB_CSV="$FIXED_DOCKERHUB_IMAGES"
          fi
          if [ "$COLLECT_DATADOG" = "true" ]; then
            DATADOG_CSV="$FIXED_DATADOG_RUM_SERVICES"
            DATADOG_ID_CSV="$FIXED_DATADOG_IDENTIFIED_SERVICES"
          fi
```

Initialising it to `""` unconditionally is what makes an unchecked box produce
`[]` rather than an unset variable.

- [ ] **Step 4: Add it to the payload**

In the same step's `jq -nc` call, add the argument and the key:

```bash
            --argjson dataDogIdentified "$(csv_to_json "$DATADOG_ID_CSV")" \
```

and, in the JSON template, immediately after `DataDogRumServices: $dataDog,`:

```
              DataDogRumIdentifiedServices: $dataDogIdentified,
```

The resolved payload is already printed to the run log and the run summary, so
this needs no extra logging. The pairs are hostnames and a service name — no user
data — so printing them is fine.

- [ ] **Step 5: Validate the workflow file**

The `jq` template is the easy thing to break. Check the YAML parses and the
payload builds:

```bash
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/run-workflow.yaml')); print('yaml ok')"
```

Then simulate the payload build in a shell, with the two helpers copied from the
workflow:

```bash
csv_to_json() {
  jq -nc --arg csv "$1" '$csv | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))'
}
csv_to_json 'conductor-ui|conductor.r1.diagrid.io,conductor-ui|dapr-ops-dashboard.diagrid.io'
csv_to_json ''
```

Expected, exactly:

```
["conductor-ui|conductor.r1.diagrid.io","conductor-ui|dapr-ops-dashboard.diagrid.io"]
[]
```

The second is the unchecked-box case, and `[]` is what makes the workflow skip
the source.

- [ ] **Step 6: Update `README.md`**

In the "At the moment the data sources include:" list, after the existing
Datadog line, add:

```markdown
- Datadog RUM authenticated sessions and identified users for the `conductor-ui`
  service on `conductor.r1.diagrid.io` and `dapr-ops-dashboard.diagrid.io`,
  including a running total of unique users
```

In the manual-run input table, change the `collect_datadog` row's effect to:

```markdown
| `collect_datadog` | checkbox | on | Datadog RUM views and users, plus `conductor-ui` authenticated sessions and identified users |
```

- [ ] **Step 7: Update `AGENTS.md`**

Add to the **Gotchas** list:

```markdown
- **`env` is a hostname on `conductor-ui`, not `prod`.** The `dev-dashboard`
  collector filters `env:prod`; that matches nothing on `conductor-ui`, whose
  `env` tag carries the deployment host (`conductor.r1.diagrid.io`,
  `dapr-ops-dashboard.diagrid.io`, and staging/local variants). This is why the
  identified-user collector takes `service|env` pairs rather than just a service
  name. Note also that `dapr-ops-dashboard.diagrid.io` reports under the
  `conductor-ui` service.
- **`@usr.anonymous_id` is not an identity on `conductor-ui`.** It is roughly
  per-session there (1411 sessions produced 1020 ids), unlike on
  `dev-dashboard`. Identified-user counting uses `@usr.id`.
```

Add to the **Database** section, after the schema sentence:

```markdown
`datadog_rum_identified_users` is the only table here holding PII: real customer
email addresses and names. **This repository is public, so GitHub Actions run
logs are world-readable — never print or log a `@usr.id`, `@usr.email` or
`@usr.name` value.** Report from `datadog_rum_identified_users_view`, which
exposes counts. Deletion for an erasure request is
`DELETE FROM datadog_rum_identified_users WHERE user_email = $1`, but it only
sticks once that user falls outside the three-week lookback.
```

- [ ] **Step 8: Commit** *(agents: ask first)*

```bash
git add .github/workflows/run-workflow.yaml README.md AGENTS.md
git commit -m "feat: collect conductor-ui identified users in CI, document it"
```

---

### Task 8: End-to-end verification with storage

**Files:**
- Modify: none

**Interfaces:**
- Consumes: everything from Tasks 2 through 7.
- Produces: rows in the live database, and the idempotency evidence that this
  feature is safe to run weekly.

> **This task writes to the live Neon database.** Get explicit confirmation
> before Step 2.

- [ ] **Step 1: Confirm the tables are still empty**

```sql
SELECT count(*) FROM datadog_rum_identified;
SELECT count(*) FROM datadog_rum_identified_users;
```

Expected: `0` and `0`. If not, a real run already happened — record the counts
before continuing, because Step 4's comparison depends on knowing the baseline.

- [ ] **Step 2: Run with storage**

**Get explicit confirmation first.**

Start `dapr run -f .`, then send the identified-users-only request from
`local-tests.http` with `"SkipStorage": false`.

- [ ] **Step 3: Verify the row counts and shape**

```sql
SELECT service, env, week_start, session_count, unique_user_count
FROM datadog_rum_identified
ORDER BY env, week_start;
```

Expected: 6 rows — three consecutive Mondays for each of the two hosts. No
`week_start` may be the Monday of the current, in-progress week.

```sql
SELECT service, env, count(*) AS users,
       count(user_email) AS with_email,
       count(user_name) AS with_name,
       count(install_date) AS with_install_date
FROM datadog_rum_identified_users
GROUP BY service, env
ORDER BY env;
```

Expected: roughly 63 users on `conductor.r1.diagrid.io` and about 3 on
`dapr-ops-dashboard.diagrid.io`. `with_email` one lower than `users` on the
production host. `with_install_date` **below** `users`, because users first seen
in the oldest week get a `NULL`.

Aggregate counts only. Do not select `user_email` or `user_name` values.

- [ ] **Step 4: Cross-check the two tables agree**

```sql
SELECT i.env, i.week_start, i.unique_user_count,
       (SELECT count(*) FROM datadog_rum_identified_users u
        WHERE u.service = i.service AND u.env = i.env
          AND (u.install_date IS NULL OR u.install_date <= i.week_start))
       AS registered_by_then
FROM datadog_rum_identified i
ORDER BY i.env, i.week_start;
```

`unique_user_count` is that week alone; `registered_by_then` is cumulative, so it
must be **greater than or equal to** the weekly count on every row, and
non-decreasing down each host's three rows. A weekly count exceeding the
cumulative one means the registry missed users the aggregate saw — the
group-by-drop symptom from Task 1.

- [ ] **Step 5: Re-run immediately and prove idempotency**

Record the current state:

```sql
SELECT count(*) AS rows, max(collection_date) AS newest FROM datadog_rum_identified;
SELECT count(*) AS rows, min(collection_date) AS oldest, count(install_date) AS dated
FROM datadog_rum_identified_users;
```

Send the same request again with `"SkipStorage": false`, then re-run those two
queries.

Expected:

- `datadog_rum_identified` — same `rows`, a **newer** `newest` (the upsert
  refreshes `collection_date` on purpose), and unchanged `session_count` /
  `unique_user_count` values.
- `datadog_rum_identified_users` — same `rows`, the **same** `oldest`
  `collection_date`, and the same `dated` count. `collection_date` and
  `install_date` are written once and never again; if `oldest` moved, the
  `do update` list has picked up a column it must not touch.

This is the check that makes the weekly schedule and the overlapping three-week
lookback safe. Do not skip it.

- [ ] **Step 6: Verify the view**

```sql
SELECT service, env, week_start, new_users, new_external_users,
       total_users, total_external_users
FROM datadog_rum_identified_users_view
ORDER BY env, week_start;
```

Expected:

- `total_users` non-decreasing down each host's rows, ending at that host's row
  count in `datadog_rum_identified_users`.
- The **earliest** `week_start` per host carrying a large `new_users` — those are
  the seeded `NULL`-`install_date` users, the cold-start artifact.
- `total_external_users` below `total_users` by roughly 14 on
  `conductor.r1.diagrid.io` (the internal `@diagrid.io` accounts).
- No row where `new_external_users` exceeds `new_users`.

- [ ] **Step 7: Audit the log for leaked PII**

Re-read the full console output of both runs. Confirm no `@usr.id`,
`@usr.email` or `@usr.name` **value** appears anywhere — including inside any
error message. Search for `@` and for `.io`/`.com` in the log to be sure.

This is the last chance to catch it before a scheduled run writes the same output
into a world-readable Actions log. If anything leaked, fix the logging and repeat
Step 2.

- [ ] **Step 8: Trigger the CI workflow manually**

From the *Actions* tab, run *Run CollectorWorkflow* with every text input set to
`none` and every checkbox **off except `collect_datadog`**, and `skip_storage`
**on**.

Verify in the run summary that the payload contains both `service|env` pairs
under `DataDogRumIdentifiedServices`, and in the run log that the six week lines
appear with no user values. Then confirm the negative case: re-run with
`collect_datadog` **off** and check the payload shows
`"DataDogRumIdentifiedServices": []` and that neither activity logs anything.

- [ ] **Step 9: Commit** *(agents: ask first)*

Nothing changed on disk in this task. If the verification queries were saved as a
scratch file, do not commit it.

---

## Deviations from the spec, with reasons

- **`Observation.UserId` rather than `Observation.Id`.** The spec's Components
  section sketches `Observation(string Id, string? Email, string? Name)` while
  its `Entry` uses `UserId`. The plan uses `UserId` in both, so the record
  property, the SQL column and the log wording all agree. No behaviour changes.
- **The registry activity logs a `with no email` count** as well as the
  `with no install date` count the spec's example line shows. It is the cheapest
  possible ongoing signal for the Task 1 group-by question: if that number ever
  jumps, attributes are being dropped again.
- **Task 3 Step 5 checks the view returns zero rows** rather than only checking
  the tables exist. The spec does not ask for it, but a view with a bad window
  function or join fails at first `SELECT`, not at `CREATE`, and finding that out
  before any data exists is much cheaper than after.

## Deferred, with reasons

- **`catalyst-ui`.** Same event shape and higher volume (4974 sessions, 124
  identified users over 28 days), so it is a one-line addition to
  `FIXED_DATADOG_IDENTIFIED_SERVICES` with no code change. Out of scope here;
  worth doing once these two pairs have a few weeks of history to compare
  against.
- **The erasure deny-list.** A deletion inside the three-week lookback is undone
  by the next run. Making it permanent needs a suppression list, which would
  itself have to store the address being suppressed. No such request exists
  today.
- **Grafana panels.** The `grafana/` export in this repo is already stale — it
  predates the `datadog_rum` tables — so refreshing it is separate work and
  should not be bundled here.
- **Unauthenticated session counts.** Decided against during design, not
  deferred: the column would have two meanings. Recorded here because the cost
  is permanent — each week's anonymous count is unrecoverable 30 days later.
