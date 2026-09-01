# DataDog RUM Weekly Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **GIT POLICY — READ FIRST:** This repository's owner has a standing global
> instruction that no agent may run `git add`, `git commit`, `git push`, or any
> other state-changing git command unless explicitly asked for that operation in
> the current conversation. The commit steps below are written for the human
> engineer. **If you are an agent, stop at each commit step and ask the user to
> confirm before running it.** Read-only git commands (`status`, `diff`, `log`)
> are fine.

> **DATABASE POLICY:** Task 4 creates tables in the live Neon database, and
> Task 6 Step 8 / Task 9 write rows to it. Do not run those without explicit
> confirmation in the current conversation, for the same reason as the git
> policy.

**Goal:** Two new Postgres tables fed from DataDog RUM as part of the existing
`CollectorWorkflow`, so the history outlives DataDog's 30-day event retention:
`datadog_rum` holds weekly session and unique-user counts per service, and
`datadog_rum_users` is an append-only registry of every distinct
`@usr.anonymous_id` seen per service, with a first-seen `install_date` where one
is knowable. Both are driven by a `DataDogRumServices` array in the workflow
start payload, holding `dev-dashboard` today.

**Architecture:** `GetDataDogRumData` computes the last three complete ISO weeks
locally (DataDog's own weekly bucketing is epoch-anchored to Thursday, so it
cannot be used), makes one `POST` per week asking for a count and a cardinality,
and upserts one row per `(service, week_start)`. `GetDataDogRumUsers` walks the same three windows, grouped by
`@usr.anonymous_id`, and inserts new ids with `on conflict do nothing` so a
recorded row is never degraded by a later run's shorter view of history.
`install_date` is the Monday of the earliest week an id was seen in — one-week
resolution. Both activities take the same
`DataDogRumInput` and the workflow fans both out over `DataDogRumServices`. Both
are idempotent by construction, so there is no retry loop.

**Tech Stack:** .NET 9 (`net9.0`), Dapr.Workflow 1.17.8, Dapr.Client 1.17.8,
`System.Text.Json`, PostgreSQL via `bindings.postgresql`, xunit for unit tests.

**Spec:** `docs/superpowers/specs/2026-08-26-datadog-rum-collection-design.md`

## Global Constraints

- Target framework is `net9.0`. Do not retarget anything.
- `Nullable` and `ImplicitUsings` are `enable`. Match that in new files.
- The C# namespace is `DaprStats` (the csproj's `RootNamespace` says
  `WorkflowSample` — ignore it and follow the code).
- `InternalsVisibleTo("CollectDaprStats.Tests")` is already configured in
  `CollectDaprStats/CollectDaprStats.csproj`. New helpers may be `internal`, but
  this plan makes them `public` to match `PostgresOutput`'s existing style.
- Existing code style: one workflow activity per file, file named after the
  activity class, `Console.WriteLine` for logging, records declared at the bottom
  of the activity's file, `const string tableName` inside the storage branch.
- Dapr binding component name is `daprstats` (`resources/postgres.yml`). Secret
  store component name is `secretstore` (`resources/secrets.yml`,
  `secretstores.local.env`).
- DataDog site is **US1**: `https://api.datadoghq.com`. Exact values, verified
  live on 2026-08-26:
  - service tag: `dev-dashboard`
  - RUM application: "Dev Dashboard", `80d4832f-54ab-4091-bd92-0d816379b40a`
  - facet: `@usr.anonymous_id`
  - env: `prod`; session type: `user`
- Secret keys, exact: `DATADOGAPIKEY` and `DATADOGAPPKEY`. RUM analytics needs
  **both**; the API key alone returns 403.
- Table names, exact: `datadog_rum` and `datadog_rum_users`. Unique constraint
  names, exact: `datadog_rum_service_week_unique` and
  `datadog_rum_users_service_user_unique`.
- Lookback is **3** complete ISO weeks, for **both** activities, from the same
  `IsoWeek.CompleteWeeksBefore(utcNow, 3)` call. Do not raise it to 4 — see
  Task 2's comment.
- The collection cron changes from `0 9 1,16 * *` to `0 9 * * 1` (weekly, Monday
  09:00 UTC). This affects every collector in the repo, not just RUM — see
  Task 8.
- `GetGitHubRepoData`'s `CollectionPeriodInDays` must equal the cron interval in
  days. It is 15 today and becomes 7 in Task 8. These two numbers are coupled:
  changing the schedule again without changing this constant silently makes
  consecutive GitHub rows overlap or leave gaps.
- Both activities are driven by the single `DataDogRumServices` array in the
  workflow start payload, and both take the same
  `DataDogRumInput(string Service, bool SkipStorage)`. The service is never
  hardcoded. `dev-dashboard` is the only entry today; more are expected, so
  nothing may assume a single service.
- Both tables carry a `service` column, and both unique constraints lead with
  it.
- Workflow code may only await `WorkflowContext` members. No `DateTime.Now`,
  `Task.Delay`, `Task.Run`, `Guid.NewGuid`, or real I/O inside
  `CollectorWorkflow`. `DateTime.UtcNow` belongs in the activity, not the
  workflow.
- Run all tests with `dotnet test dapr-stats.sln`.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `CollectDaprStats/IsoWeek.cs` | Create | Pure ISO-week arithmetic: which complete weeks to fetch, and their UTC bounds |
| `CollectDaprStats/DataDogRumResponse.cs` | Create | Pure parser for the RUM aggregate response body |
| `CollectDaprStats/GetDataDogRumData.cs` | Create | Weekly counts activity: secrets, HTTP, logging, upsert. Owns `DataDogRumInput` |
| `CollectDaprStats/DataDogRumUsers.cs` | Create | Pure: parse one week's grouped response, and derive install dates across weeks |
| `CollectDaprStats/GetDataDogRumUsers.cs` | Create | User-registry activity, service passed in. Reuses `DataDogRumInput` |
| `CollectDaprStats/Program.cs` | Modify | Registers both activities |
| `CollectDaprStats/CollectorWorkflow.cs` | Modify | `DataDogRumServices` on the input record, plus the fan-out block calling both activities |
| `CollectDaprStats/secrets.json.example` | Modify | Documents the two new secret keys |
| `postgres/postgres_schema.psql` | Modify | `datadog_rum` and `datadog_rum_users` table definitions |
| `.github/workflows/run-workflow.yaml` | Modify | Weekly cron, two `env:` entries, and the new payload field |
| `CollectDaprStats/GetGitHubRepoData.cs` | Modify | Collection window 15 -> 7 days, to match the new weekly cadence |
| `local-tests.http` | Modify | New payload field, plus a RUM-only request |
| `CollectDaprStats.Tests/IsoWeekTests.cs` | Create | Tests for week arithmetic |
| `CollectDaprStats.Tests/DataDogRumResponseTests.cs` | Create | Tests for the weekly response parser |
| `CollectDaprStats.Tests/DataDogRumUsersTests.cs` | Create | Tests for id parsing and install-date derivation |

The pure helpers are split out of the activities because an activity itself
cannot be unit tested without an HTTP harness this project does not have.
Everything worth testing lives in `IsoWeek`, `DataDogRumResponse` and
`DataDogRumUsers` — including all of the install-date reasoning, which is the
subtlest logic in the feature. The activities are left as thin glue.

---

### Task 1: Confirm the DataDog request and response shape

This is a gate, not code. Three details in the spec come from the generated API
client schema rather than a live call: whether the `aggregation` enum is
lowercase, whether response computes are keyed `c0`/`c1`, and what key the
grouped response puts the facet value under. Tasks 3 and 6 depend on the answers,
so settle them before writing either parser.

**Files:**
- Create: `scratch/datadog-week-response.json` (captured fixture, not committed)
- Create: `scratch/datadog-grouped-response.json` (captured fixture, not committed)

**Interfaces:**
- Consumes: nothing.
- Produces: real response bodies for the Task 3 and Task 6 test fixtures, plus
  confirmation of the compute key names and the grouping key.

- [ ] **Step 1: Create the DataDog keys**

In DataDog (US1), create an API key and an **Application** key. Export them:

```bash
export DD_API_KEY='<api key>'
export DD_APP_KEY='<application key>'
```

- [ ] **Step 2: Call the endpoint for a known week**

```bash
mkdir -p scratch
curl -s -X POST "https://api.datadoghq.com/api/v2/rum/analytics/aggregate" \
  -H "DD-API-KEY: $DD_API_KEY" \
  -H "DD-APPLICATION-KEY: $DD_APP_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "compute": [
      { "aggregation": "count",       "type": "total" },
      { "aggregation": "cardinality", "type": "total", "metric": "@usr.anonymous_id" }
    ],
    "filter": {
      "from":  "2026-08-17T00:00:00Z",
      "to":    "2026-08-24T00:00:00Z",
      "query": "@type:session @session.type:user service:dev-dashboard env:prod"
    }
  }' | tee scratch/datadog-week-response.json
```

- [ ] **Step 3: Check the response against the design-time numbers**

Expected: HTTP 200, and a single bucket whose computes are **64 sessions and 25
unique users** — the values the DataDog connector returned on 2026-08-26 for
that week.

Note the exact shape of the response for Task 3. Record which of these you saw:

- Are computes keyed `c0`/`c1`, or by something else?
- Is the bucket array at `data.buckets`, or somewhere else?

**If the week of 2026-08-17 has aged out of retention** (it leaves the 30-day
window around 2026-09-16), the response will be a partial or zero count. In that
case pick the most recent complete Monday-to-Monday week instead, and verify the
numbers against the RUM Explorer UI in DataDog for the same window rather than
against 64/25.

**If `count`/`cardinality` are rejected**, retry with `COUNT`/`CARDINALITY` and
note which casing the API accepted — Task 5's request body must match.

- [ ] **Step 4: Capture a grouped response for the same week**

This is the shape Task 6's `ParseIds` is written against. Same endpoint and the
same window as Step 2, but grouped by the anonymous id instead of computing a
cardinality:

```bash
curl -s -X POST "https://api.datadoghq.com/api/v2/rum/analytics/aggregate"   -H "DD-API-KEY: $DD_API_KEY"   -H "DD-APPLICATION-KEY: $DD_APP_KEY"   -H "Content-Type: application/json"   -d '{
    "compute":  [ { "aggregation": "count", "type": "total" } ],
    "group_by": [ { "facet": "@usr.anonymous_id", "limit": 1000 } ],
    "filter": {
      "from":  "2026-08-17T00:00:00Z",
      "to":    "2026-08-24T00:00:00Z",
      "query": "@type:session @session.type:user service:dev-dashboard env:prod"
    }
  }' | tee scratch/datadog-grouped-response.json
```

Expected: HTTP 200 and one bucket per distinct anonymous id, each shaped like

```json
{ "by": { "@usr.anonymous_id": "b7a6a386-..." }, "computes": { "c0": 7 } }
```

Record which of these you saw:

- Is the id under `by["@usr.anonymous_id"]`, or under a different key?
- Is there a bucket with an empty or absent `by` — sessions carrying no
  anonymous id? Task 6 skips those; confirm whether any exist.
- **How many buckets came back?** For the week of 2026-08-17 the cardinality in
  Step 2 was 25, so expect 25 buckets. If the two disagree, the grouped query is
  dropping sessions that have no anonymous id — worth knowing, because it means
  `unique_user_count` and the registry will not agree exactly.

Use the same week as Step 2 so the two numbers are comparable, and switch both to
a recent week together if 2026-08-17 has aged out.

- [ ] **Step 5: Stop and report**

If either shape differs from `data.buckets[0].computes.c0` / `.c1` or from
`data.buckets[].by["@usr.anonymous_id"]`, report the real shape before
continuing. Tasks 3, 5 and 6 need amending, and it is cheaper to do that now than
to debug a parser against a guessed schema.

- [ ] **Step 6: No commit**

`scratch/` is a working directory. Nothing to commit in this task. Do not commit
the captured responses — they contain no secrets, but they are not source. Note
that the grouped response contains real anonymous user ids, which is another
reason to leave it out of git.

---

### Task 2: `IsoWeek` week arithmetic

**Files:**
- Create: `CollectDaprStats/IsoWeek.cs`
- Test: `CollectDaprStats.Tests/IsoWeekTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public sealed record IsoWeek.Window(DateOnly WeekStart, DateTime From, DateTime To)`
  - `public static IReadOnlyList<IsoWeek.Window> IsoWeek.CompleteWeeksBefore(DateTime utcNow, int count)`
    — returns `count` windows, **oldest first**. `From` is Monday `00:00:00Z`,
    `To` is the following Monday `00:00:00Z`, both `DateTimeKind.Utc`.
    Task 5 calls this with `(DateTime.UtcNow, 3)`.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/IsoWeekTests.cs`. The expected dates below were
computed from the calendar, not guessed: 2026-08-26 is a Wednesday, 2026-08-24 a
Monday, 2026-08-23 a Sunday, 2027-01-01 a Friday, and 2026-12-28 a Monday.

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class IsoWeekTests
{
    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string[] WeekStarts(DateTime utcNow, int count) =>
        IsoWeek.CompleteWeeksBefore(utcNow, count)
            .Select(w => w.WeekStart.ToString("yyyy-MM-dd"))
            .ToArray();

    [Fact]
    public void CompleteWeeksBefore_Midweek_ReturnsThreeCompleteWeeksOldestFirst()
    {
        // Wednesday. The week starting Mon 2026-08-24 is in progress and excluded.
        var weeks = WeekStarts(Utc("2026-08-26T12:00:00Z"), 3);

        Assert.Equal(new[] { "2026-08-03", "2026-08-10", "2026-08-17" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_ExactlyMondayMidnight_ExcludesTheWeekJustStarted()
    {
        // The week 08-17..08-24 has only just completed, so it is the newest one.
        var weeks = WeekStarts(Utc("2026-08-24T00:00:00Z"), 3);

        Assert.Equal(new[] { "2026-08-03", "2026-08-10", "2026-08-17" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_LateSunday_StillExcludesTheCurrentWeek()
    {
        // Sunday 23:59:59Z. DayOfWeek.Sunday is 0, which is the case a naive
        // (int)DayOfWeek - 1 calculation gets wrong.
        var weeks = WeekStarts(Utc("2026-08-23T23:59:59Z"), 3);

        Assert.Equal(new[] { "2026-07-27", "2026-08-03", "2026-08-10" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_AcrossYearBoundary_WalksBackIntoThePreviousYear()
    {
        // Friday 2027-01-01. Its ISO week starts Mon 2026-12-28.
        var weeks = WeekStarts(Utc("2027-01-01T09:00:00Z"), 3);

        Assert.Equal(new[] { "2026-12-07", "2026-12-14", "2026-12-21" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_WindowsAreContiguousSevenDayUtcSpans()
    {
        var weeks = IsoWeek.CompleteWeeksBefore(Utc("2026-08-26T12:00:00Z"), 3);

        foreach (var week in weeks)
        {
            Assert.Equal(DateTimeKind.Utc, week.From.Kind);
            Assert.Equal(DateTimeKind.Utc, week.To.Kind);
            Assert.Equal(TimeSpan.FromDays(7), week.To - week.From);
            Assert.Equal(week.WeekStart, DateOnly.FromDateTime(week.From));
            Assert.Equal(DayOfWeek.Monday, week.From.DayOfWeek);
        }

        Assert.Equal(weeks[0].To, weeks[1].From);
        Assert.Equal(weeks[1].To, weeks[2].From);
    }

    [Fact]
    public void CompleteWeeksBefore_NewestWindowNeverOverlapsTheCurrentWeek()
    {
        var utcNow = Utc("2026-08-26T12:00:00Z");

        var newest = IsoWeek.CompleteWeeksBefore(utcNow, 3).Last();

        Assert.True(newest.To <= utcNow);
    }

    [Fact]
    public void CompleteWeeksBefore_CountBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IsoWeek.CompleteWeeksBefore(Utc("2026-08-26T12:00:00Z"), 0));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~IsoWeekTests`

Expected: build failure — `The type or namespace name 'IsoWeek' could not be
found`.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/IsoWeek.cs`:

```csharp
namespace DaprStats
{
    /// <summary>
    /// ISO week arithmetic in UTC. Weeks run Monday 00:00:00Z to the following
    /// Monday 00:00:00Z.
    /// </summary>
    /// <remarks>
    /// Week boundaries are computed here rather than delegated to DataDog's
    /// `interval` bucketing, which is epoch-anchored and therefore produces
    /// Thursday-aligned weeks.
    /// </remarks>
    public static class IsoWeek
    {
        public sealed record Window(DateOnly WeekStart, DateTime From, DateTime To);

        /// <summary>
        /// The <paramref name="count"/> most recently completed ISO weeks,
        /// oldest first. The week containing <paramref name="utcNow"/> is in
        /// progress and is never returned.
        /// </summary>
        public static IReadOnlyList<Window> CompleteWeeksBefore(DateTime utcNow, int count)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "At least one week must be requested.");
            }

            var currentWeekStart = StartOfIsoWeek(DateOnly.FromDateTime(utcNow));

            var windows = new List<Window>(count);
            for (var weeksBack = count; weeksBack >= 1; weeksBack--)
            {
                var weekStart = currentWeekStart.AddDays(-7 * weeksBack);
                windows.Add(new Window(
                    weekStart,
                    MidnightUtc(weekStart),
                    MidnightUtc(weekStart.AddDays(7))));
            }

            return windows;
        }

        private static DateOnly StartOfIsoWeek(DateOnly date)
        {
            // DayOfWeek is Sunday-based (Sunday == 0), ISO weeks are Monday-based.
            var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-daysSinceMonday);
        }

        private static DateTime MidnightUtc(DateOnly date) =>
            DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~IsoWeekTests`

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/IsoWeek.cs CollectDaprStats.Tests/IsoWeekTests.cs
git commit -m "feat: add ISO week arithmetic for RUM collection windows"
```

---

### Task 3: `DataDogRumResponse` parser

**Files:**
- Create: `CollectDaprStats/DataDogRumResponse.cs`
- Test: `CollectDaprStats.Tests/DataDogRumResponseTests.cs`

**Interfaces:**
- Consumes: the response shape confirmed in Task 1.
- Produces:
  - `public static (long SessionCount, long UniqueUserCount) DataDogRumResponse.Parse(ReadOnlySpan<byte> json)`
    — throws `FormatException` on anything it cannot read. Task 5 calls it with
    the raw response bytes.

**Before starting:** if Task 1 found a response shape other than
`data.buckets[0].computes.c0` / `.c1`, adjust the JSON in these tests and the
property names in the implementation to match what the API actually returned.
The signature and the error behaviour do not change.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/DataDogRumResponseTests.cs`:

```csharp
using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumResponseTests
{
    private static (long SessionCount, long UniqueUserCount) Parse(string json) =>
        DataDogRumResponse.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_SingleBucket_ReadsCountThenCardinality()
    {
        // Shape and values captured from the live API for the week of 2026-08-17.
        var (sessions, users) = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 64, "c1": 25 } }
                ]
              }
            }
            """);

        Assert.Equal(64, sessions);
        Assert.Equal(25, users);
    }

    [Fact]
    public void Parse_EmptyBuckets_ReturnsZeroes()
    {
        // A week with no traffic at all. A real zero is worth storing.
        var (sessions, users) = Parse("""
            { "meta": { "status": "done" }, "data": { "buckets": [] } }
            """);

        Assert.Equal(0, sessions);
        Assert.Equal(0, users);
    }

    [Fact]
    public void Parse_FloatingPointComputes_TruncatesToLong()
    {
        // Aggregate computes come back as JSON numbers and are not always integral.
        var (sessions, users) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 64.0, "c1": 25.0 } } ] } }
            """);

        Assert.Equal(64, sessions);
        Assert.Equal(25, users);
    }

    [Fact]
    public void Parse_NullCompute_ReadsAsZero()
    {
        var (sessions, users) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 12, "c1": null } } ] } }
            """);

        Assert.Equal(12, sessions);
        Assert.Equal(0, users);
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumResponse.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": { "status": "done" } }"""));
    }

    [Fact]
    public void Parse_NoBucketsArray_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "data": { "buckets": {} } }"""));
    }

    [Fact]
    public void Parse_MissingCompute_Throws()
    {
        Assert.Throws<FormatException>(
            () => Parse("""{ "data": { "buckets": [ { "computes": { "c0": 64 } } ] } }"""));
    }

    [Fact]
    public void Parse_NonNumericCompute_Throws()
    {
        Assert.Throws<FormatException>(
            () => Parse("""{ "data": { "buckets": [ { "computes": { "c0": "64", "c1": 25 } } ] } }"""));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~DataDogRumResponseTests`

Expected: build failure — `The type or namespace name 'DataDogRumResponse' could
not be found`.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/DataDogRumResponse.cs`:

```csharp
using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parses a RUM analytics aggregate response. Computes are keyed
    /// positionally by request order, so `c0` is the first compute in the
    /// request body and `c1` the second.
    /// </summary>
    public static class DataDogRumResponse
    {
        private const string SessionCountKey = "c0";
        private const string UniqueUserCountKey = "c1";

        public static (long SessionCount, long UniqueUserCount) Parse(ReadOnlySpan<byte> json)
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

            if (buckets.GetArrayLength() == 0)
            {
                // No sessions matched the query. That is a real zero week.
                return (0, 0);
            }

            if (!buckets[0].TryGetProperty("computes", out var computes))
            {
                throw new FormatException("DataDog response bucket has no computes object.");
            }

            return (ReadCount(computes, SessionCountKey),
                    ReadCount(computes, UniqueUserCountKey));
        }

        private static long ReadCount(JsonElement computes, string key)
        {
            if (!computes.TryGetProperty(key, out var value))
            {
                throw new FormatException($"DataDog response has no compute '{key}'.");
            }

            return value.ValueKind switch
            {
                // Counts arrive as JSON numbers that are not always integral.
                JsonValueKind.Number => (long)value.GetDouble(),
                JsonValueKind.Null => 0L,
                _ => throw new FormatException(
                    $"DataDog compute '{key}' is {value.ValueKind}, expected a number."),
            };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~DataDogRumResponseTests`

Expected: PASS, 9 tests.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test dapr-stats.sln`

Expected: PASS. The pre-existing 16 tests plus 7 from Task 2 plus 9 here = 32.

- [ ] **Step 6: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/DataDogRumResponse.cs CollectDaprStats.Tests/DataDogRumResponseTests.cs
git commit -m "feat: add DataDog RUM aggregate response parser"
```

---

### Task 4: Create the `datadog_rum` and `datadog_rum_users` tables

**Files:**
- Modify: `postgres/postgres_schema.psql`

**Interfaces:**
- Consumes: nothing.
- Produces: the `datadog_rum` table with its
  `datadog_rum_service_week_unique` constraint (Task 5's `on conflict` targets
  it), and the `datadog_rum_users` table with its
  `datadog_rum_users_service_user_unique` constraint (Task 6's `on conflict`
  targets that one).

> **This task writes to the live Neon database.** Get explicit confirmation
> before Step 3.

- [ ] **Step 1: Add the table to the schema file**

In `postgres/postgres_schema.psql`, after the `CREATE TABLE youtube_dapr (...)`
block and before the first `---` separator that precedes the views, add:

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

Then, immediately after it, the registry table:

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

`install_date` is the only nullable column in either table, deliberately — it is
`NULL` when the id's first sighting is pinned to the retention floor and its real
first day cannot be known.

The unique key leads with `service`, matching `datadog_rum`. That means
`install_date` is tracked per service, so one browser visiting two Diagrid front
ends gets a separate first-seen date on each. To count distinct people across all
services, use `count(distinct user_anonymous_id)` rather than `count(*)`.

No views accompany these tables. Every other source stores a cumulative counter
and needs a view to derive a rate; `datadog_rum` is already weekly and
`datadog_rum_users` is a registry, so Grafana queries both directly.

- [ ] **Step 2: Dry-run the DDL against the live database**

There is no `psql` on the dev machine and the Neon CLI has no SQL subcommand;
queries go through Neon's HTTP SQL endpoint. Get the connection string with:

```bash
neon connection-string --project-id spring-pine-41263944 \
  --role-name marcduiker --database-name daprstats
```

Verify the statement compiles without committing it, by wrapping it in a `DO`
block that aborts. The SQL contains single quotes and `$$`, so build the JSON
payload with a script rather than fighting shell quoting:

```bash
mkdir -p scratch
python - <<'PY'
import json
sql = """
DO $$
BEGIN
  CREATE TABLE datadog_rum (
      id SERIAL PRIMARY KEY,
      service VARCHAR(255) NOT NULL,
      week_start DATE NOT NULL,
      session_count BIGINT NOT NULL,
      unique_user_count BIGINT NOT NULL,
      collection_date TIMESTAMP NOT NULL,
      CONSTRAINT datadog_rum_service_week_unique UNIQUE (service, week_start)
  );
  CREATE TABLE datadog_rum_users (
      id SERIAL PRIMARY KEY,
      service VARCHAR(255) NOT NULL,
      user_anonymous_id VARCHAR(64) NOT NULL,
      install_date DATE,
      collection_date TIMESTAMP NOT NULL,
      CONSTRAINT datadog_rum_users_service_user_unique UNIQUE (service, user_anonymous_id)
  );
  RAISE EXCEPTION 'DRY RUN OK - rolling back';
END $$;
"""
json.dump({"query": sql, "params": []}, open("scratch/ddl.json", "w"))
PY

CS='<connection string from step above>'
curl -s -X POST "https://ep-super-silence-67141105.eu-central-1.aws.neon.tech/sql" \
  -H "Neon-Connection-String: $CS" -H "Content-Type: application/json" \
  -d @scratch/ddl.json
```

Expected: `"code":"P0001"` with the message `DRY RUN OK - rolling back`. That
means both `CREATE TABLE` statements executed and were then rolled back by the
exception — neither table exists yet. Any other SQLSTATE is a real error in the
DDL.

- [ ] **Step 3: Apply it for real** (ask for confirmation first)

Re-generate `scratch/ddl.json` with the two bare `CREATE TABLE` statements — no
`DO` wrapper, no `RAISE` — and POST it the same way. Expected: no error object.
The Neon `/sql` endpoint takes one statement per request, so send them as two
requests rather than one.

- [ ] **Step 4: Verify the table and constraint exist**

```sql
-- via the same /sql endpoint
select table_name, column_name, data_type, is_nullable
from information_schema.columns
where table_name in ('datadog_rum', 'datadog_rum_users')
order by table_name, ordinal_position;

select conrelid::regclass as tbl, conname from pg_constraint
where conrelid in ('datadog_rum'::regclass, 'datadog_rum_users'::regclass);
```

Expected: six columns on `datadog_rum` and five on `datadog_rum_users`, in the
order declared. `install_date` must be the only row with `is_nullable = YES`
across both tables. Both unique constraints present alongside their primary
keys.

- [ ] **Step 5: Commit** (agents: ask first — see GIT POLICY)

```bash
git add postgres/postgres_schema.psql
git commit -m "feat: add datadog_rum table"
```

---

### Task 5: `GetDataDogRumData` activity

**Files:**
- Create: `CollectDaprStats/GetDataDogRumData.cs`
- Modify: `CollectDaprStats/Program.cs`

**Interfaces:**
- Consumes:
  - `IsoWeek.CompleteWeeksBefore(DateTime, int)` and `IsoWeek.Window` (Task 2)
  - `DataDogRumResponse.Parse(ReadOnlySpan<byte>)` (Task 3)
  - `PostgresOutput.InsertAsync(string sqlText, object[] sqlParameters)` (existing)
  - the `datadog_rum` table (Task 4)
- Produces:
  - `public record DataDogRumInput(string Service, bool SkipStorage)` — Tasks 6 and 7
    constructs this.
  - the activity class name `GetDataDogRumData`, which Task 7 references via
    `nameof`.

- [ ] **Step 1: Write the activity**

Create `CollectDaprStats/GetDataDogRumData.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetDataDogRumData : WorkflowActivity<DataDogRumInput, bool>
    {
        // Complete ISO weeks fetched per run. Three is the largest lookback that
        // is always inside DataDog's 30-day RUM retention: at most 6 days of the
        // current partial week plus 21 days of complete weeks is 27 days. Four
        // weeks reaches 34 days, and a week only partly inside the retention
        // window returns a partial count that looks like a real low week.
        private const int WeeksPerRun = 3;

        // US1. Confirmed 2026-08-26.
        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumData(
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
            DataDogRumInput input)
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
                        await FetchWeekAsync(input.Service, week, apiKey, appKey);

                    Console.WriteLine(
                        $"DataDog RUM {input.Service} week {week.WeekStart:yyyy-MM-dd}: " +
                        $"{sessionCount} sessions, {uniqueUserCount} unique users");

                    if (!input.SkipStorage)
                    {
                        await StoreAsync(
                            input.Service, week.WeekStart, sessionCount, uniqueUserCount);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM data for {input.Service} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<(long SessionCount, long UniqueUserCount)> FetchWeekAsync(
            string service, IsoWeek.Window week, string apiKey, string appKey)
        {
            // @session.type:user excludes Synthetics traffic and env:prod excludes
            // a staging deployment. Neither exists today; both stop a future one
            // from silently inflating the counts.
            var searchQuery =
                $"@type:session @session.type:user service:{service} env:prod";

            var body = JsonSerializer.Serialize(new
            {
                compute = new object[]
                {
                    new { aggregation = "count", type = "total" },
                    new
                    {
                        aggregation = "cardinality",
                        type = "total",
                        metric = "@usr.anonymous_id"
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
                throw new HttpRequestException(
                    $"DataDog returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return DataDogRumResponse.Parse(payload);
        }

        private async Task StoreAsync(
            string service, DateOnly weekStart, long sessionCount, long uniqueUserCount)
        {
            const string tableName = "datadog_rum";

            // The unique constraint is what makes the overlapping lookback safe:
            // re-collecting a week overwrites it instead of duplicating it.
            var sqlText =
                $"insert into {tableName} " +
                "(service, week_start, session_count, unique_user_count, collection_date) " +
                "values ($1, $2::date, $3, $4, $5) " +
                "on conflict (service, week_start) do update " +
                "set session_count = excluded.session_count, " +
                "    unique_user_count = excluded.unique_user_count, " +
                "    collection_date = excluded.collection_date";

            var sqlParameters = new object[]
            {
                service,
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

    public record DataDogRumInput(string Service, bool SkipStorage);
}
```

Two notes on choices that differ from a naive reading of the spec:

- `week_start` is passed as a `yyyy-MM-dd` string with an explicit `$2::date`
  cast rather than as a `DateOnly`. The Dapr binding serialises parameters to
  JSON, and a text-to-date cast is predictable where `DateOnly` serialisation is
  not.
- The spec sketched a `DataDogRumData` record. It is not in this implementation:
  nothing carries the tuple across a boundary, so the record would be a field
  bag with one use site.

- [ ] **Step 2: Register the activity**

In `CollectDaprStats/Program.cs`, inside `builder.Services.AddDaprWorkflow(...)`,
after `options.RegisterActivity<GetDiagridDashboardData>();`:

```csharp
    options.RegisterActivity<GetDataDogRumData>();
```

No new DI registrations are needed — `IHttpClientFactory`, `PostgresOutput` and
`DaprClient` are already registered.

- [ ] **Step 3: Build**

Run: `dotnet build dapr-stats.sln`

Expected: build succeeds, no new warnings.

- [ ] **Step 4: Run the whole test suite**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, 32 tests. Nothing here is unit tested — the activity is glue,
and its two testable pieces already have coverage from Tasks 2 and 3. It is
exercised for real in Task 9.

- [ ] **Step 5: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/GetDataDogRumData.cs CollectDaprStats/Program.cs
git commit -m "feat: add DataDog RUM collection activity"
```

---

### Task 6: `DataDogRumUsers` and the user-registry activity

**Files:**
- Create: `CollectDaprStats/DataDogRumUsers.cs`
- Create: `CollectDaprStats/GetDataDogRumUsers.cs`
- Modify: `CollectDaprStats/Program.cs`
- Test: `CollectDaprStats.Tests/DataDogRumUsersTests.cs`

**Interfaces:**
- Consumes:
  - the grouped response shape confirmed in Task 1 Step 4
  - `IsoWeek.CompleteWeeksBefore(DateTime, int)` and `IsoWeek.Window` (Task 2)
  - `DataDogRumInput(string Service, bool SkipStorage)` (Task 5)
  - `PostgresOutput.InsertAsync(string sqlText, object[] sqlParameters)` (existing)
  - the `datadog_rum_users` table (Task 4)
- Produces:
  - `public sealed record DataDogRumUsers.Entry(string AnonymousId, DateOnly? InstallDate)`
  - `public static IReadOnlyList<string> DataDogRumUsers.ParseIds(ReadOnlySpan<byte> json)`
  - `public static IReadOnlyList<Entry> DataDogRumUsers.DeriveEntries(IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<string> Ids)> weeksOldestFirst)`
  - the activity class name `GetDataDogRumUsers`, which Task 7 references via
    `nameof`

It reuses `DataDogRumInput(string Service, bool SkipStorage)` from Task 5 rather
than declaring its own input record — both activities are per-service and take
the same two values, so a second record would be duplication.

**Before starting:** if Task 1 Step 4 found that the grouped response puts the id
somewhere other than `buckets[].by["@usr.anonymous_id"]`, change the JSON in
these tests and the property lookup in `ParseIds` to match. Nothing else moves.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/DataDogRumUsersTests.cs`:

```csharp
using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumUsersParseIdsTests
{
    private static IReadOnlyList<string> Parse(string json) =>
        DataDogRumUsers.ParseIds(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseIds_ReturnsEveryGroupedId()
    {
        // Shape captured from the live API: one bucket per id, grouped by facet.
        var ids = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": { "@usr.anonymous_id": "aaa" }, "computes": { "c0": 7 } },
                  { "by": { "@usr.anonymous_id": "bbb" }, "computes": { "c0": 2 } }
                ]
              }
            }
            """);

        Assert.Equal(new[] { "aaa", "bbb" }, ids);
    }

    [Fact]
    public void ParseIds_NoBuckets_ReturnsEmpty()
    {
        Assert.Empty(Parse("""{ "data": { "buckets": [] } }"""));
    }

    [Fact]
    public void ParseIds_BucketMissingTheFacet_IsSkippedNotThrown()
    {
        // Sessions with no anonymous id are grouped into a bucket without the
        // facet key. There is no id to register, so skip it.
        var ids = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 1 } },
                  { "by": { "@usr.anonymous_id": "aaa" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Equal(new[] { "aaa" }, ids);
    }

    [Fact]
    public void ParseIds_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumUsers.ParseIds(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseIds_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": {} }"""));
    }
}

public class DataDogRumUsersDeriveEntriesTests
{
    // Real consecutive ISO week starts: Mondays 2026-08-03, 08-10, 08-17, 08-24.
    private static readonly DateOnly[] Mondays =
    [
        new(2026, 8, 3), new(2026, 8, 10), new(2026, 8, 17), new(2026, 8, 24)
    ];

    private static (DateOnly, IReadOnlyList<string>) Week(int index, params string[] ids) =>
        (Mondays[index], ids);

    private static DateOnly? InstallDateOf(
        IReadOnlyList<DataDogRumUsers.Entry> entries, string id) =>
        entries.Single(e => e.AnonymousId == id).InstallDate;

    [Fact]
    public void DeriveEntries_IdInTheOldestWeekWithData_HasNoInstallDate()
    {
        // "aaa" was already active when the window opened, so its real first week
        // is unknowable and must not be guessed.
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "aaa", "bbb")]);

        Assert.Null(InstallDateOf(entries, "aaa"));
    }

    [Fact]
    public void DeriveEntries_IdFirstSeenAfterTheOldestWeek_GetsThatMonday()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "aaa", "bbb"), Week(2, "bbb")]);

        Assert.Equal(new DateOnly(2026, 8, 10), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_IdInManyWeeks_KeepsTheEarliest()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "bbb"), Week(2, "bbb"), Week(3, "bbb")]);

        Assert.Equal(new DateOnly(2026, 8, 10), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_LeadingEmptyWeeksDoNotCountAsTheBoundary()
    {
        // Weeks 0 and 1 returned nothing, so the boundary is week 2 — the oldest
        // week that actually had data.
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0), Week(1), Week(2, "aaa"), Week(3, "bbb")]);

        Assert.Null(InstallDateOf(entries, "aaa"));
        Assert.Equal(new DateOnly(2026, 8, 24), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_EveryIdAppearsExactlyOnce()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa", "bbb"), Week(1, "aaa", "bbb"), Week(2, "aaa")]);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "aaa", "bbb" }, entries.Select(e => e.AnonymousId).Order());
    }

    [Fact]
    public void DeriveEntries_EveryInstallDateIsAMonday()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "bbb"), Week(2, "ccc")]);

        foreach (var entry in entries.Where(e => e.InstallDate is not null))
        {
            Assert.Equal(DayOfWeek.Monday, entry.InstallDate!.Value.DayOfWeek);
        }
    }

    [Fact]
    public void DeriveEntries_NoDataAtAll_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumUsers.DeriveEntries([Week(0), Week(1)]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~DataDogRumUsers`

Expected: build failure — `The type or namespace name 'DataDogRumUsers' could not
be found`.

- [ ] **Step 3: Write the pure helper**

Create `CollectDaprStats/DataDogRumUsers.cs`:

```csharp
using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parsing and first-seen derivation for the anonymous user registry.
    /// </summary>
    public static class DataDogRumUsers
    {
        private const string Facet = "@usr.anonymous_id";

        public sealed record Entry(string AnonymousId, DateOnly? InstallDate);

        /// <summary>
        /// The distinct anonymous ids in one week's grouped aggregate response.
        /// </summary>
        public static IReadOnlyList<string> ParseIds(ReadOnlySpan<byte> json)
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

            var ids = new List<string>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                // Sessions with no anonymous id group into a bucket without the
                // facet key. There is nothing to register, so skip it.
                // This is why the registry count can be below datadog_rum's
                // unique_user_count for the same week.
                if (bucket.TryGetProperty("by", out var by) &&
                    by.TryGetProperty(Facet, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var id = value.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        /// <summary>
        /// One entry per distinct id, with the start of the earliest ISO week it
        /// was observed in.
        /// </summary>
        /// <param name="weeksOldestFirst">
        /// Must be ordered oldest week first. Weeks with no ids are allowed.
        /// </param>
        /// <remarks>
        /// An id first seen in the oldest week that returned any data gets a null
        /// install date: that week is the edge of what was queried, so the id was
        /// probably active before it and its true first week is unknowable.
        /// </remarks>
        public static IReadOnlyList<Entry> DeriveEntries(
            IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<string> Ids)> weeksOldestFirst)
        {
            var firstSeen = new Dictionary<string, DateOnly>();
            DateOnly? boundary = null;

            foreach (var (weekStart, ids) in weeksOldestFirst)
            {
                if (ids.Count == 0)
                {
                    // An empty week is not the boundary -- the boundary is the
                    // oldest week that actually produced ids.
                    continue;
                }

                boundary ??= weekStart;

                foreach (var id in ids)
                {
                    firstSeen.TryAdd(id, weekStart);
                }
            }

            return firstSeen
                .Select(kv => new Entry(kv.Key, kv.Value == boundary ? null : kv.Value))
                .ToList();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~DataDogRumUsers`

Expected: PASS, 12 tests.

- [ ] **Step 5: Write the activity**

Create `CollectDaprStats/GetDataDogRumUsers.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetDataDogRumUsers : WorkflowActivity<DataDogRumInput, bool>
    {
        // The same three complete ISO weeks GetDataDogRumData collects, so
        // install_date always lands on a Monday that has a datadog_rum row.
        private const int WeeksPerRun = 3;

        // DataDog's default maximum for field grouping. Current volume is ~80
        // distinct ids per 30 days, so a single week is nowhere near it.
        private const int GroupByLimit = 1000;

        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumUsers(
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
            DataDogRumInput input)
        {
            var apiKeySecrets = await _daprClient.GetSecretAsync(SecretStore, ApiKeySecret);
            var appKeySecrets = await _daprClient.GetSecretAsync(SecretStore, AppKeySecret);
            var apiKey = apiKeySecrets[ApiKeySecret];
            var appKey = appKeySecrets[AppKeySecret];

            // CompleteWeeksBefore returns oldest first, which is exactly the
            // order DeriveEntries needs.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var collected = new List<(DateOnly WeekStart, IReadOnlyList<string> Ids)>();
            var allSucceeded = true;

            foreach (var week in weeks)
            {
                try
                {
                    var ids = await FetchWeekAsync(input.Service, week, apiKey, appKey);

                    if (ids.Count >= GroupByLimit)
                    {
                        Console.WriteLine(
                            $"WARNING: week {week.WeekStart:yyyy-MM-dd} returned " +
                            $"{ids.Count} ids, at the group-by limit of {GroupByLimit}. " +
                            "Ids are being dropped.");
                    }

                    collected.Add((week.WeekStart, ids));
                }
                catch (Exception ex)
                {
                    // A missing week costs precision on install_date for ids first
                    // seen that week, and the next run refetches it anyway. It
                    // cannot corrupt a stored row, because the insert is
                    // `do nothing`.
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM users for {input.Service} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            var entries = DataDogRumUsers.DeriveEntries(collected);

            var withoutInstallDate = entries.Count(e => e.InstallDate is null);
            Console.WriteLine(
                $"DataDog RUM users {input.Service}: {entries.Count} distinct anonymous " +
                $"ids over {collected.Count} weeks ({withoutInstallDate} with no " +
                "install date)");

            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input.Service, entry);
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<string>> FetchWeekAsync(
            string service, IsoWeek.Window week, string apiKey, string appKey)
        {
            var body = JsonSerializer.Serialize(new
            {
                compute = new object[] { new { aggregation = "count", type = "total" } },
                group_by = new object[]
                {
                    new { facet = "@usr.anonymous_id", limit = GroupByLimit }
                },
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query = $"@type:session @session.type:user service:{service} env:prod"
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

            return DataDogRumUsers.ParseIds(payload);
        }

        private async Task StoreAsync(string service, DataDogRumUsers.Entry entry)
        {
            const string tableName = "datadog_rum_users";

            // `do nothing`, not `do update`: a later run has a strictly shorter
            // view of history, so it must never overwrite a recorded first date.
            var sqlText =
                $"insert into {tableName} " +
                "(service, user_anonymous_id, install_date, collection_date) " +
                "values ($1, $2, $3::date, $4) " +
                "on conflict (service, user_anonymous_id) do nothing";

            var sqlParameters = new object[]
            {
                service,
                entry.AnonymousId,
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

No input record is declared here — `DataDogRumInput` from Task 5 is reused.

**On the null `install_date` parameter:** `entry.InstallDate?.ToString(...)` is
`null` for floor-pinned ids, and the `!` suppresses the nullable warning on an
`object[]` element. Step 8 confirms that a `null` array element reaches Postgres
as SQL `NULL` through the Dapr binding's JSON parameter serialisation rather than
as the text `"null"`. If it does not, switch that parameter to `DBNull.Value`, or
branch to a second SQL statement that omits the column entirely.

- [ ] **Step 6: Register the activity**

In `CollectDaprStats/Program.cs`, immediately after
`options.RegisterActivity<GetDataDogRumData>();`:

```csharp
    options.RegisterActivity<GetDataDogRumUsers>();
```

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`

Expected: build succeeds; PASS, 44 tests — the 32 from the pre-existing suite
plus Tasks 2 and 3, plus 12 here.

- [ ] **Step 8: Confirm a NULL install_date round-trips**

This is the one behaviour in this task that unit tests cannot reach. With the
tables created (Task 4), insert a row by hand through the Neon `/sql` endpoint
with `install_date` as SQL `null`, read it back, then clean up:

```sql
insert into datadog_rum_users (service, user_anonymous_id, install_date, collection_date)
values ('dev-dashboard', 'null-probe', null, now());

select service, user_anonymous_id, install_date from datadog_rum_users
where user_anonymous_id = 'null-probe';

delete from datadog_rum_users where user_anonymous_id = 'null-probe';
```

Expected: `install_date` reads back as `null`. That proves the column accepts it;
Task 9 Step 4 then proves the *binding* sends it correctly for real floor-pinned
ids, which is the part that could silently write the text `"null"` instead.

- [ ] **Step 9: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/DataDogRumUsers.cs CollectDaprStats/GetDataDogRumUsers.cs \
        CollectDaprStats/Program.cs CollectDaprStats.Tests/DataDogRumUsersTests.cs
git commit -m "feat: collect distinct DataDog RUM anonymous user ids"
```

---

### Task 7: Wire the activities into `CollectorWorkflow`

**Files:**
- Modify: `CollectDaprStats/CollectorWorkflow.cs`

**Interfaces:**
- Consumes: `DataDogRumInput(string Service, bool SkipStorage)` (Task 5) and the
  activity names `GetDataDogRumData` (Task 5) and `GetDataDogRumUsers` (Task 6).
  Both activities take the same input record.
- Produces: the `DataDogRumServices` field on `CollectorWorkflowInput`, which
  Task 8's JSON payloads populate.

- [ ] **Step 1: Add the field to the workflow input record**

At the bottom of `CollectDaprStats/CollectorWorkflow.cs`, add
`DataDogRumServices` to `CollectorWorkflowInput`, immediately before
`SkipStorage`:

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

One field drives both collectors. There is no scenario where you want the weekly
counts for a service but not its user registry, so a second flag would only add a
way to get them out of step.

- [ ] **Step 2: Add the fan-out block**

In `RunAsync`, after the `if (input.CollectDiagridDashboardData)` block and
before `if (input.CollectGitHubData)`:

```csharp
            if (input.DataDogRumServices?.Length > 0)
            {
                var getDataDogRumDataTasks = new List<Task>();
                foreach (var service in input.DataDogRumServices)
                {
                    getDataDogRumDataTasks.Add(context.CallActivityAsync(
                        nameof(GetDataDogRumData),
                        new DataDogRumInput(service, input.SkipStorage)));
                    getDataDogRumDataTasks.Add(context.CallActivityAsync(
                        nameof(GetDataDogRumUsers),
                        new DataDogRumInput(service, input.SkipStorage)));
                }
                await Task.WhenAll(getDataDogRumDataTasks);
            }
```

The null-conditional `?.` is deliberate and differs from the sibling blocks,
which dereference their arrays directly. `local-tests.http` contains payloads
that will not list the new field, and those requests must keep working rather
than fail with a `NullReferenceException`.

Both activities are queued for every service, so N services means 2N activities
under one `Task.WhenAll`. Each makes 3 requests, so a service costs 6 per run —
6 today, 24 if all four Diagrid front ends were onboarded. The NuGet loop alone
already makes about fifty.

- [ ] **Step 3: Build**

Run: `dotnet build dapr-stats.sln`

Expected: build succeeds.

- [ ] **Step 4: Run the whole test suite**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, 44 tests. No test constructs `CollectorWorkflowInput`, so adding
a positional field does not break the suite. If a compile error says otherwise,
fix the call site rather than reordering the record.

- [ ] **Step 5: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/CollectorWorkflow.cs
git commit -m "feat: call DataDog RUM collection from CollectorWorkflow"
```

---

### Task 8: Secrets and CI wiring

**Files:**
- Modify: `CollectDaprStats/secrets.json.example`
- Modify: `.github/workflows/run-workflow.yaml`
- Modify: `CollectDaprStats/GetGitHubRepoData.cs:28`
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: the `DataDogRumServices` field (Task 7) and the secret key names
  `DATADOGAPIKEY` / `DATADOGAPPKEY` (Task 5).
- Produces: a runnable scheduled collection.

- [ ] **Step 1: Document the secrets**

`CollectDaprStats/secrets.json.example` — add both keys with empty values,
following the existing naming in that file:

```json
{
    "PostgreSQLConnection": "",
    "DaprDiscordServerId" : "",
    "DiscordBotToken" : "",
    "DaprStatsGitHubPAT" : "",
    "DataDogApiKey" : "",
    "DataDogAppKey" : ""
}
```

The secret store is `secretstores.local.env`, which reads environment variables,
so the names the code looks up are the uppercase env var names
(`DATADOGAPIKEY`, `DATADOGAPPKEY`). This file is documentation of which values an
operator has to supply; it follows the mixed-case convention already used for
the other four.

- [ ] **Step 2: Add your own local secrets**

In `CollectDaprStats/secrets.json` (git-ignored, not the `.example`), set the two
real values. Task 9 needs them.

- [ ] **Step 3: Change the schedule to weekly**

In `.github/workflows/run-workflow.yaml`, replace the `schedule` block and its
comment:

```yaml
on:
  schedule:
    # Run at 10:00 CET (09:00 UTC) every Monday
    - cron: '0 9 * * 1'
  workflow_dispatch:
```

The previous value was `'0 9 1,16 * *'` with the comment
`# Run at 10:00 CET (09:00 UTC) on day 1 and 16 of each month`.

Three things to get right in those five fields:

- **Day-of-week, not day-of-month.** `1,8,15,22` looks weekly but leaves a 29- to
  31-day gap at every month boundary. Only the day-of-week field gives exactly
  seven days between runs.
- **Monday.** ISO weeks close at Monday `00:00:00Z`, so a Monday 09:00 run
  collects a week that closed nine hours earlier. Any other weekday leaves the
  newest complete week uncollected for up to six extra days.
- **09:00 UTC, unchanged**, preserving the existing 10:00 CET slot.

**This changes the cadence for every collector in the repo**, not just RUM. NuGet,
npm, PyPI, Docker Hub, GitHub, Discord and Diagrid all start running weekly. That
is an improvement rather than a risk: the `days_diff` divisor in the
download-count views drops from ~15 to ~7, raising the resolution of every
increment series. Those views normalise to a weekly rate either way, so no
historical row is invalidated and no view needs changing — this is exactly the
divisor the `EXTRACT(DAY ...)` fix made accurate.

- [ ] **Step 4: Match the GitHub collection window to the new cadence**

`GetGitHubRepoData` queries GitHub for a fixed window and stores that window
alongside the counts. The window does not track the cron, so moving to weekly
runs without touching it would make consecutive rows overlap by 8 days and
double-count the same commits, issues and PRs.

In `CollectDaprStats/GetGitHubRepoData.cs:28`:

```csharp
                const int CollectionPeriodInDays = 7;
```

The previous value was `15`. That single constant feeds all five uses — the
`Since` filters on commits, issues, comments and pull requests
(`GetGitHubRepoData.cs:33-36`) and the `CollectedOverNumberOfDays` written to the
row (`GetGitHubRepoData.cs:56`) — so one edit keeps the query window and the
stored divisor in step. Do not hardcode 7 anywhere else.

**Historical rows are unaffected and stay comparable.**
`weekly_github_counts_dapr` divides by each row's *own*
`collected_over_number_of_days`, so existing rows keep dividing by 15 and new
rows divide by 7. Both are correct weekly rates for their own window, and the
series stays continuous across the change. This is the payoff of storing the
window per row rather than assuming it in the view.

Nothing else in the repo needs a matching change:

- **npm** already uses a 7-day window (`GetNpmPackageData.cs:36`), which was
  *mismatched* under the old fortnightly cadence — 8 of every 15 days were never
  sampled. Weekly runs fix that for free.
- **PyPI** uses 30 days (`GetPythonPackageData.cs:34`) because that is what the
  PyPI API returns. It cannot be made to tile and was already a moving average;
  it just gets smoother.
- **NuGet, Docker Hub and Diagrid** are cumulative counters whose views derive
  `days_diff` from consecutive `collection_date`s, so they follow the cadence
  automatically.

- [ ] **Step 5: Add the secrets to CI**

In `.github/workflows/run-workflow.yaml`, extend the `env:` block:

```yaml
env:
  POSTGRESQLCONNECTION: ${{ secrets.POSTGRESQLCONNECTION }}
  DAPRSTATSGITHUBPAT: ${{ secrets.DAPRSTATSGITHUBPAT }}
  DISCORDBOTTOKEN: ${{ secrets.DISCORDBOTTOKEN }}
  DAPRDISCORDSERVERID: ${{ secrets.DAPRDISCORDSERVERID }}
  DATADOGAPIKEY: ${{ secrets.DATADOGAPIKEY }}
  DATADOGAPPKEY: ${{ secrets.DATADOGAPPKEY }}
```

Then add both as GitHub repository secrets (Settings → Secrets and variables →
Actions). Without them the activity fails on every run while the rest of the
collection succeeds.

- [ ] **Step 6: Add the field to the CI payload**

In the same file, in the `Generate Variables and Start CollectorWorkflow` step's
`curl` body, add the field after `"DockerHubImages"`:

```
              "DockerHubImages": ["daprio/daprd", "daprio/scheduler", "daprio/operator", "daprio/injector", "daprio/sentry", "daprio/placement"],
              "DataDogRumServices": ["dev-dashboard"],
              "SkipStorage": false
```

`DataDogRumServices` is the single switch for both RUM collectors. When another
front end is onboarded, add its `service` tag value to this array — nothing else
changes.

- [ ] **Step 7: Add the field to the local test requests**

`local-tests.http` already has six requests. Two changes:

**(a)** Add `"DataDogRumServices" : ["dev-dashboard"],` to the *complete*
CollectorWorkflow request (line 8's body, after `"DockerHubImages"`).

Leave the other five requests alone — they deliberately test paths without these
collectors, and Task 7's `?.` guard means an absent field is valid input. One of
them omitting it is itself the regression test for Step 8 of Task 9.

**(b)** Append a new request at the end of the file. The file defines
`@dapr_url`, `@workflow_id={{$guid}}` and `@currentDate={{$datetime iso8601}}` at
the top and uses ` : ` (spaces either side of the colon) for every field except
`SkipStorage`; follow that exactly:

```
###
### DataDog RUM only, no storage. Logs three complete ISO weeks for
### dev-dashboard and writes nothing. Completes in seconds.
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
    "DataDogRumServices" : ["dev-dashboard"],
    "SkipStorage": true
}
```

This one request exercises both RUM collectors, since the array drives both. Task
9 sends it with `SkipStorage` flipped between `true` and `false`, so keep it easy
to edit.

- [ ] **Step 8: Verify the YAML still parses**

```bash
python -c "import yaml; d = yaml.safe_load(open('.github/workflows/run-workflow.yaml', encoding='utf-8')); print(d['on' if 'on' in d else True]['schedule'])"
```

Expected: `[{'cron': '0 9 * * 1'}]`. Two quirks the command works around: PyYAML
parses the bare key `on` as the boolean `True`, and the file contains UTF-8 emoji
that Windows will mis-decode without the explicit `encoding='utf-8'`. Verified
against the pre-change file, where it printed `[{'cron': '0 9 1,16 * *'}]`.

- [ ] **Step 9: Commit** (agents: ask first — see GIT POLICY)

```bash
git add CollectDaprStats/secrets.json.example .github/workflows/run-workflow.yaml \
        CollectDaprStats/GetGitHubRepoData.cs local-tests.http
git commit -m "feat: weekly collection cadence, DataDog RUM secrets and payload"
```

---

### Task 9: End-to-end verification

**Files:** none modified. This task runs the thing.

**Interfaces:**
- Consumes: everything from Tasks 2–8.
- Produces: confidence, three rows in `datadog_rum`, and one row per distinct
  anonymous id in `datadog_rum_users`.

- [ ] **Step 1: Start the app with Dapr**

```bash
dapr run -f .
```

Wait for the app to report ready.

- [ ] **Step 2: Dry run with `SkipStorage: true`**

Send the "DataDog RUM only, no storage" request added in Task 8 Step 5, with
`"SkipStorage": true`.

Expected in the app log: three lines, oldest week first, of the form

```
DataDog RUM dev-dashboard week 2026-08-03: 68 sessions, 17 unique users
DataDog RUM dev-dashboard week 2026-08-10: 37 sessions, 19 unique users
DataDog RUM dev-dashboard week 2026-08-17: 64 sessions, 25 unique users
```

The week starts must be Mondays, and the newest must be the most recently
completed week. Counts will differ from these figures — they are from
Thursday-aligned buckets measured during design, except the 08-17 week which was
measured as a true Monday week and was 64/25.

- [ ] **Step 3: Check the registry output from the same run**

The request in Step 2 runs both collectors, so the same log also carries one
registry line, of the form

```
DataDog RUM users dev-dashboard: 61 distinct anonymous ids over 3 weeks (N with no install date)
```

The id count covers three weeks, so expect somewhat fewer than the 80 measured
across a full 30-day window on 2026-08-26. `N` is however many ids appeared in
the oldest of the three weeks; on a first run that will be a substantial
fraction, and it shrinks to near zero on later runs — a week is first collected
while it is the *newest* of the three, so it gets a real date then and
`do nothing` preserves it.

If any week logs a `WARNING` about the group-by limit, stop — ids are being
dropped and `GroupByLimit` needs raising.

- [ ] **Step 4: Cross-check against the DataDog UI**

In DataDog, open RUM Explorer with the query
`@type:session @session.type:user service:dev-dashboard env:prod`.

- Set the range to one of the three weeks (Monday 00:00 to Monday 00:00, UTC) and
  confirm the session count matches Step 2's logged number for that week.
- Set the range to the three collected weeks together (Monday to Monday, UTC),
  group by `@usr.anonymous_id`, and confirm the distinct count matches Step 3's
  id count.
- For the newest week alone, confirm the distinct count equals that week's
  `unique_user_count` in `datadog_rum`. The two activities now query identical
  windows, so these must agree — unless some sessions carry no anonymous id, in
  which case the grouped count is lower by exactly that many. Task 1 Step 4
  established whether any such sessions exist.

This is the step that catches a wrong timezone or an off-by-one window, which no
unit test can.

- [ ] **Step 5: Real run with `SkipStorage: false`**

Send the Step 2 request again with `"SkipStorage": false`.

Then query both tables through the Neon HTTP `/sql` endpoint:

```sql
select service, week_start, session_count, unique_user_count, collection_date
from datadog_rum order by week_start;

select service,
       count(*) as ids,
       count(install_date) as with_install_date,
       count(*) - count(install_date) as null_install_date,
       min(install_date) as earliest,
       max(install_date) as latest
from datadog_rum_users group by service;
```

Expected: exactly three rows in `datadog_rum`, week starts on consecutive
Mondays, counts matching Step 2's log lines. In `datadog_rum_users`, one group
for `dev-dashboard` whose `ids` matches Step 3's count and whose
`null_install_date` matches its `N`. Every row must have
`service = 'dev-dashboard'` — a blank or missing service means the parameter is
not being bound.

**Check the NULLs are real SQL nulls**, which is the failure mode Task 6 Step 8
flagged:

```sql
select count(*) from datadog_rum_users where install_date::text = 'null';
```

Expected: `0`. A non-zero result means the binding serialised the null parameter
as the text `"null"`, and Task 6's `StoreAsync` needs the `DBNull.Value` fix.

- [ ] **Step 6: Verify idempotency**

Send the same request a second time, then re-run both queries.

Expected for `datadog_rum`: still exactly **three** rows, not six.
`collection_date` has advanced on all three; the counts are unchanged.

Expected for `datadog_rum_users`: the **same row count**, and — the part that
matters — `install_date` and `collection_date` unchanged on every row. Capture
them before and after to be sure:

```sql
select md5(string_agg(
    service || user_anonymous_id || coalesce(install_date::text, 'null')
      || collection_date::text,
    '' order by service, user_anonymous_id)) as fingerprint
from datadog_rum_users;
```

Expected: an identical fingerprint before and after the second run. A changed
fingerprint means `on conflict do nothing` was written as `do update`, which
would silently degrade every install date on every future run.

If `datadog_rum` has six rows, the unique constraint from Task 4 is missing or
the `on conflict` clause does not name it correctly.

- [ ] **Step 7: Verify the full collection still works**

Send the full collection request with `"SkipStorage": true` — the one with every
package, image and flag populated.

Expected: workflow reaches `COMPLETED`, and both the weekly RUM lines and the
user-registry line appear alongside the existing collectors' output. This confirms
the new blocks did not break the ordering or the `Task.WhenAll` fan-out.

The registry adds only 3 requests per service, so this is well inside the
workflow's 25-minute polling deadline in `.github/workflows/run-workflow.yaml`.
No timing change is needed there.

- [ ] **Step 8: Verify install_date joins to datadog_rum**

Because both activities now walk the same windows, every non-null `install_date`
must be a Monday that exists in `datadog_rum`:

```sql
select u.install_date, count(*) as ids
from datadog_rum_users u
left join datadog_rum r
  on r.service = u.service and r.week_start = u.install_date
where u.install_date is not null and r.week_start is null
group by u.install_date;
```

Expected: no rows. Any result means `install_date` is not landing on an ISO week
boundary, so the two activities are not sharing
`IsoWeek.CompleteWeeksBefore` as intended.

- [ ] **Step 9: Verify a payload without the new field**

Send any request that omits `DataDogRumServices` entirely.

Expected: workflow reaches `COMPLETED` and simply skips the RUM block. A
`NullReferenceException` here means the `?.` from Task 7 Step 2 was dropped.

Then send one with `"DataDogRumServices" : []`. Same expectation: both RUM
collectors skipped, workflow `COMPLETED`.

- [ ] **Step 10: Trigger the CI workflow**

Run `run-workflow.yaml` via `workflow_dispatch`.

Expected: the run reports `COMPLETED`, the log shows the three RUM lines, and
querying both tables shows rows whose `collection_date` matches the CI run.
This is the only step that proves the GitHub secrets are wired correctly.

Also confirm the GitHub window change from Task 8 Step 4 took effect:

```sql
select collected_over_number_of_days, count(*) as rows,
       min(collection_date)::date as first, max(collection_date)::date as last
from github_dapr group by 1 order by 1;
```

Expected: the pre-existing rows still showing `15`, and the rows from this run
showing `7`. Both values coexisting is correct — `weekly_github_counts_dapr`
divides by each row's own value, so the weekly rate stays comparable across the
change. If the new rows still say `15`, the constant was not rebuilt into the
deployed app.

- [ ] **Step 11: Remove the scratch fixtures**

```bash
rm -rf scratch
```

- [ ] **Step 12: Commit** (agents: ask first — see GIT POLICY)

Nothing to commit unless earlier steps needed fixes. If they did, commit those
with a message describing the fix rather than the verification.

---

## Deviations from the spec, with reasons

- **No `DataDogRumData` record.** The spec sketched one. Nothing carries the
  tuple across a boundary, so it would be a single-use field bag; the activity
  passes the values directly to `StoreAsync`.
- **`week_start` is bound as text with an explicit `::date` cast**, not as a
  `DateOnly`. The spec did not specify the binding type. Text-to-date is
  predictable through the Dapr binding's JSON parameter serialisation; `DateOnly`
  serialisation through that path is not.
- **The activity returns `false` if *any* week failed**, while committing the
  weeks that succeeded. The spec said "logs and returns `false`" without
  addressing partial failure. Per-week independence is the useful behaviour and
  the spec's Error handling section already argues for it.

## Deferred, with reasons

- **A Grafana panel.** Out of scope; the tables are queryable as soon as Task 9
  passes, and panel layout is a UI decision.
- **Day-accurate `install_date`.** Achievable by querying per day instead of per
  week — the same `ParseIds` and `DeriveEntries` work unchanged, only the window
  list differs. It costs 30 requests per service per run instead of 3, for
  precision that was explicitly not required.
- **Anything that resolves an anonymous id to a person.** The registry is a list
  of browser-generated ids; joining them to accounts would need `@usr.id`, which
  this application does not set.
- **Enabling Conductor UI or Catalyst UI now.** Both tables are keyed by
  `service` and both collectors read the `DataDogRumServices` array, so adding one
  is a single entry in the CI payload with no schema or code change. It is
  deferred only because their `service` tag values have not been verified against
  the live org — do not add them blind. Verify the tag the same way
  `dev-dashboard` was verified, by inspecting a raw session event for that
  application.
- **The other two RUM applications** (Conductor UI, Catalyst UI). Adding either
  is one string in the `DataDogRumServices` array once someone wants it — but
  their `service` tag values have not been verified, so do not add them blind.
- **RUM-based custom metrics for long-horizon session history.** Would break the
  30-day retention ceiling for `session_count`, but cannot express a distinct
  count and would put configuration in the DataDog UI. See the spec's Known
  limitations.
- **Recording the SDK sample rate.** Currently `session_sample_rate: 100`, so
  counts are complete. If it is ever lowered, stored counts silently become
  samples. A `sample_rate` column would make that visible; not worth it until
  sampling is actually used.
