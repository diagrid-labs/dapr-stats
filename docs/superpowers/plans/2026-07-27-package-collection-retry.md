# Package Collection Retry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **GIT POLICY — READ FIRST:** This repository's owner has a standing global
> instruction that no agent may run `git add`, `git commit`, `git push`, or any
> other state-changing git command unless explicitly asked for that operation in
> the current conversation. The commit steps below are written for the human
> engineer. **If you are an agent, stop at each commit step and ask the user to
> confirm before running it.** Read-only git commands (`status`, `diff`, `log`)
> are fine.

**Goal:** Make `CollectorWorkflow` detect package rows that failed to reach Postgres because of upstream 429 rate limiting, and re-collect only the missing packages after a 5-minute pause, for at most three passes per ecosystem.

**Architecture:** A new `PostgresOutput.ReadAsync` reads the existing `*_latest_packages_view` views through the Dapr PostgreSQL binding's `query` operation. Three new `Check*PackageData` activities diff the requested package names against what the view holds for the collection date and return the missing names. `CollectorWorkflow` wraps each ecosystem in a `Get → Check → Sleep` loop, and the three loops run in parallel under `Task.WhenAll`.

**Tech Stack:** .NET 9 (`net9.0`), Dapr.Workflow 1.17.8, Dapr.Client 1.17.8, PostgreSQL via `bindings.postgresql`, xunit for unit tests.

**Spec:** `docs/superpowers/specs/2026-07-27-package-collection-retry-design.md`

## Status (as of 2026-07-28)

- **All implementation and verification steps are done.** Tasks 1–8 complete, apart from the commit steps. `dotnet test dapr-stats.sln` passes 16/16 and the feature has been verified end to end against the real Postgres binding.
- **Task 1's gate passed:** the binding's `query` operation returns positional two-element arrays with RFC3339 `Z` timestamps, on all three views. `ParseRows` is correct as written.
- **The retry loop was verified doing real work,** not just simulated. pypistats was rate-limiting throughout the session, so a genuine 429 on `dapr-agents` was detected by `CheckPythonPackageData`, backed off 5 minutes, retried only that one package, and succeeded (Step 3). Separately, the `MaxAttempts` bound was confirmed to stop after 3 rounds (Step 4).
- **Nothing is committed.** All changes are in the working tree on branch `harden-stats-collection`. Every commit step below is still open, per the git policy at the top.
- **Two gaps worth knowing:**
  - Step 5 was run with only the three package ecosystems. The DockerHub, Discord, Diagrid, and GitHub blocks were not executed — they are unchanged code, but the literal full-request path is unverified.
  - The `.github/workflows/run-workflow.yaml` polling change (Task 7) was verified only by YAML parse and step-order inspection. It has not run in CI.
- **New bug found, not fixed:** `GetPythonPackageData` returns `false` on any non-2xx with no log line and no exception (`GetPythonPackageData.cs:46`), so a 429 is currently invisible. `GetNpmPackageData` and `GetNuGetPackageData` share the shape. This is the status quo the Check activities compensate for; a one-line `Console.WriteLine` on the failure branch would make it diagnosable directly. Out of scope here.

## Global Constraints

- Target framework for all projects is `net9.0`. The local SDK is 10.0.102; do not retarget anything to net10.0.
- `Nullable` and `ImplicitUsings` are `enable` in `CollectDaprStats.csproj`. Match that in the test project.
- The C# namespace is `DaprStats` (note: `RootNamespace` in the csproj says `WorkflowSample` — ignore it and follow the code, which uses `DaprStats`).
- Existing code style: one workflow activity per file, file named after the activity class, `Console.WriteLine` for logging, records declared at the bottom of the activity's file.
- Dapr binding component name is `daprstats` (`resources/postgres.yml`).
- Postgres view names, exact: `python_dapr_latest_packages_view`, `nuget_dapr_client_latest_packages_view`, `npm_dapr_dapr_latest_packages_view`.
- Retry bound: `MaxAttempts = 3` means **3 iterations of the loop body** — the initial pass plus at most 2 retries, so at most 2 backoff sleeps.
- Backoff is `TimeSpan.FromMinutes(5)`. Existing per-package spacing stays as it is: 5s for NuGet, 5s for npm, 10s for Python.
- Do not change `Get*PackageData` behaviour. Aligning their inserted `DateTime.UtcNow` with `input.CollectionDate` is explicitly out of scope.
- Workflow code may only await `WorkflowContext` members. No `DateTime.Now`, `Task.Delay`, `Task.Run`, `Guid.NewGuid`, or real I/O inside `CollectorWorkflow`.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `local-tests.http` | Modify | Manual request collection; gains a raw binding-query request and single-ecosystem workflow requests |
| `CollectDaprStats/PostgresOuput.cs` | Modify | Adds `PackageCollectionRecord`, `ReadAsync`, `ParseRows`, `ParseDate`; extracts binding name/operation consts |
| `CollectDaprStats/PackageDataChecker.cs` | Create | Reads a view and diffs it against expected package names. Owns the pure `FindMissing` and `ToCollectionDate` helpers |
| `CollectDaprStats/CheckPythonPackageData.cs` | Create | Activity for `python_dapr_latest_packages_view` |
| `CollectDaprStats/CheckNuGetPackageData.cs` | Create | Activity for `nuget_dapr_client_latest_packages_view` |
| `CollectDaprStats/CheckNpmPackageData.cs` | Create | Activity for `npm_dapr_dapr_latest_packages_view` |
| `CollectDaprStats/Program.cs` | Modify | Registers the three activities and `PackageDataChecker` |
| `CollectDaprStats/CollectorWorkflow.cs` | Modify | Three parallel `Get → Check → Sleep` loops replacing the three sequential package blocks |
| `CollectDaprStats/CollectDaprStats.csproj` | Modify | `InternalsVisibleTo` for the test project |
| `CollectDaprStats.Tests/CollectDaprStats.Tests.csproj` | Create | xunit test project |
| `CollectDaprStats.Tests/PostgresOutputParseRowsTests.cs` | Create | Tests for row/date parsing |
| `CollectDaprStats.Tests/PackageDataCheckerTests.cs` | Create | Tests for `FindMissing` and `ToCollectionDate` |
| `dapr-stats.sln` | Modify | Adds the test project |
| `.github/workflows/build.yml` | Modify | Builds the solution and runs `dotnet test` |
| `.github/workflows/run-workflow.yaml` | Modify | `timeout-minutes: 30` and a polling wait step |

---

### Task 1: Confirm the binding query response shape

Everything downstream assumes the Dapr PostgreSQL binding's `query` operation returns rows as positional JSON arrays. That is documented Dapr behaviour but has not been observed against this component. **This task is a gate: if the shape differs, stop and revise the spec before writing any parsing code.**

No production code changes here.

**Files:**
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: nothing
- Produces: a confirmed answer to "what JSON does `query` return", which Task 2's `ParseRows` is written against

- [x] **Step 1: Add a raw binding-query request to `local-tests.http`**

Append to the end of the file:

```http
###
### Read the latest stored Python packages directly through the binding.
### Used to confirm the shape of the `query` response.
###
POST {{dapr_url}}/v1.0/bindings/daprstats
Content-Type: application/json

{
    "operation": "query",
    "metadata": {
        "sql": "select package_name, collection_date from python_dapr_latest_packages_view"
    }
}
```

- [x] **Step 2: Start the app with Dapr** *(run by the user in their own terminal, so the secrets stay out of the agent's tool calls)*

Requires `POSTGRESQLCONNECTION`, `DAPRSTATSGITHUBPAT`, `DISCORDBOTTOKEN`, and `DAPRDISCORDSERVERID` in the environment (see `dapr.yaml`).

Run: `dapr run -f .`
Expected: `collect-dapr-stats` starts and logs the registered workflows and activities.

- [x] **Step 3: Send the request and record the raw response**

Send the request added in Step 1 (via the VS Code REST Client, or the equivalent `curl`):

```bash
curl -s -X POST http://localhost:3500/v1.0/bindings/daprstats \
  -H "Content-Type: application/json" \
  -d '{"operation":"query","metadata":{"sql":"select package_name, collection_date from python_dapr_latest_packages_view"}}'
```

Expected — an array of two-element arrays, package name first, date second:

```json
[["dapr","2026-07-27T00:00:00Z"],["dapr-agents","2026-07-27T00:00:00Z"],["dapr-ext-workflow","2026-07-27T00:00:00Z"]]
```

- [x] **Step 4: Evaluate the gate** — **PASSED, 2026-07-28.** All three views return positional two-element arrays with an RFC3339 `Z` timestamp, HTTP 200:

```
python: [["dapr","2026-07-24T00:00:00Z"],["dapr-agents","2026-07-24T00:00:00Z"],["dapr-ext-workflow","2026-07-24T00:00:00Z"]]
nuget:  [["CommunityToolkit.Aspire.Hosting.Dapr","2026-07-24T00:00:00Z"], … 8 rows …]
npm:    [["@dapr/dapr","2026-07-24T00:00:00Z"]]
```

Consequences: `ParseRows`' positional-array assumption is confirmed; the `DateTimeOffset.Parse` branch of `ParseDate` is the live one (the `yyyy-MM-dd` branch is defensive only); all three view names in the Check activities are correct; and the NuGet view returns the requested names with exact casing, so the deferred name-drift risk is not currently active.

Note the exact form of the second element — it decides which branch of `ParseDate` in Task 2 actually runs. Both are implemented and tested, so either is fine:
- `"2026-07-27T00:00:00Z"` (RFC3339 timestamp), or
- `"2026-07-27"` (plain date string).

**STOP and revise the spec if instead you see:** named objects (`[{"package_name":"dapr",...}]`), an envelope object (`{"rows":[...]}`), or a base64 string. Task 2 onward assumes positional arrays.

- [~] **Step 5: Stop the app** — deliberately skipped; the app stays up to feed Task 8, which would only restart it.

Run: `dapr stop -f .`

- [ ] **Step 6: Commit** *(agents: ask first — see git policy at top)*

```bash
git add local-tests.http
git commit -m "test: add raw binding query request for the latest packages view"
```

---

### Task 2: Test project and row parsing

**Files:**
- Create: `CollectDaprStats.Tests/CollectDaprStats.Tests.csproj` (via `dotnet new`)
- Create: `CollectDaprStats.Tests/PostgresOutputParseRowsTests.cs`
- Modify: `CollectDaprStats/PostgresOuput.cs`
- Modify: `CollectDaprStats/CollectDaprStats.csproj`
- Modify: `dapr-stats.sln`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- Consumes: the confirmed response shape from Task 1
- Produces:
  - `public record PackageCollectionRecord(string PackageName, DateOnly CollectionDate)` in namespace `DaprStats`
  - `internal static PackageCollectionRecord[] PostgresOutput.ParseRows(ReadOnlySpan<byte> json)`

- [x] **Step 1: Scaffold the test project and wire it up**

```bash
dotnet new xunit -n CollectDaprStats.Tests -o CollectDaprStats.Tests -f net9.0
dotnet sln dapr-stats.sln add CollectDaprStats.Tests/CollectDaprStats.Tests.csproj
dotnet add CollectDaprStats.Tests/CollectDaprStats.Tests.csproj reference CollectDaprStats/CollectDaprStats.csproj
```

Then confirm `CollectDaprStats.Tests/CollectDaprStats.Tests.csproj` has `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` in its `PropertyGroup`; add them if the template omitted them.

Delete the template's placeholder test file if one was generated (`UnitTest1.cs`).

- [x] **Step 2: Grant the test project access to internals**

Add this `ItemGroup` to `CollectDaprStats/CollectDaprStats.csproj`:

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>CollectDaprStats.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
```

- [x] **Step 3: Write the failing tests**

Create `CollectDaprStats.Tests/PostgresOutputParseRowsTests.cs`:

```csharp
using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class PostgresOutputParseRowsTests
{
    private static PackageCollectionRecord[] Parse(string json) =>
        PostgresOutput.ParseRows(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseRows_MapsColumnsByPosition()
    {
        var rows = Parse("""[["dapr","2026-07-27T00:00:00Z"],["dapr-agents","2026-07-27T00:00:00Z"]]""");

        Assert.Equal(2, rows.Length);
        Assert.Equal("dapr", rows[0].PackageName);
        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
        Assert.Equal("dapr-agents", rows[1].PackageName);
    }

    [Fact]
    public void ParseRows_PlainDateString_ParsesCalendarDate()
    {
        var rows = Parse("""[["dapr","2026-07-27"]]""");

        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_UtcTimestamp_KeepsSameCalendarDate()
    {
        var rows = Parse("""[["dapr","2026-07-27T00:00:00Z"]]""");

        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_OffsetTimestamp_NormalizesToUtcBeforeTakingDate()
    {
        // 2026-07-27T00:00:00+02:00 is 2026-07-26T22:00:00Z.
        // This fails if the parser uses the host's local timezone instead of UTC.
        var rows = Parse("""[["dapr","2026-07-27T00:00:00+02:00"]]""");

        Assert.Equal(new DateOnly(2026, 7, 26), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_EmptyPayload_ReturnsEmpty()
    {
        Assert.Empty(PostgresOutput.ParseRows(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseRows_NullLiteral_ReturnsEmpty()
    {
        Assert.Empty(Parse("null"));
    }

    [Fact]
    public void ParseRows_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(Parse("[]"));
    }
}
```

- [x] **Step 4: Run the tests to verify they fail** *(done in an earlier session; not re-verifiable now that the code exists)*

Run: `dotnet test CollectDaprStats.Tests/CollectDaprStats.Tests.csproj`
Expected: compile error — `PackageCollectionRecord` and `PostgresOutput.ParseRows` do not exist.

- [x] **Step 5: Implement `ParseRows`**

Rewrite `CollectDaprStats/PostgresOuput.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Dapr.Client;

namespace DaprStats
{
    public class PostgresOutput
    {
        private const string BindingName = "daprstats";
        private const string ExecOperation = "exec";
        private const string QueryOperation = "query";

        private readonly DaprClient _daprClient;

        public PostgresOutput(DaprClient daprClient)
        {
            _daprClient = daprClient;
        }

        public async Task InsertAsync(string sqlText, object[] sqlParameters)
        {
            var paramsText = JsonSerializer.Serialize(sqlParameters);
            var metadata = new Dictionary<string, string>
            {
                {"sql", sqlText},
                {"params", paramsText}
            };

            const string data = "";

            await _daprClient.InvokeBindingAsync(BindingName, ExecOperation, data, metadata);
        }

        internal static PackageCollectionRecord[] ParseRows(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                return [];
            }

            var rows = JsonSerializer.Deserialize<JsonElement[][]>(json) ?? [];

            return rows
                .Select(row => new PackageCollectionRecord(row[0].GetString()!, ParseDate(row[1])))
                .ToArray();
        }

        private static DateOnly ParseDate(JsonElement element)
        {
            var text = element.GetString()!;

            // A bare `yyyy-MM-dd` must not go through DateTimeOffset.Parse, which
            // would attach the host's local offset and can shift the calendar day.
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                return date;
            }

            var offset = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            return DateOnly.FromDateTime(offset.UtcDateTime);
        }
    }

    public record PackageCollectionRecord(string PackageName, DateOnly CollectionDate);
}
```

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CollectDaprStats.Tests/CollectDaprStats.Tests.csproj`
Expected: PASS, 7 tests.

- [x] **Step 7: Make CI build the solution and run the tests**

In `.github/workflows/build.yml`, replace the `Restore dependencies` and `Build solution` steps with:

```yaml
        - name: Restore dependencies
          run: dotnet restore dapr-stats.sln

        - name: Build solution
          run: dotnet build dapr-stats.sln --configuration Release --no-restore

        - name: Run unit tests
          run: dotnet test dapr-stats.sln --configuration Release --no-build --verbosity normal
```

- [x] **Step 8: Verify the whole solution builds**

Run: `dotnet build dapr-stats.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats.Tests dapr-stats.sln .github/workflows/build.yml CollectDaprStats/CollectDaprStats.csproj CollectDaprStats/PostgresOuput.cs
git commit -m "feat: parse positional binding query rows into PackageCollectionRecord"
```

---

### Task 3: `PostgresOutput.ReadAsync`

The thin binding call that feeds `ParseRows`. Not unit tested — it is one `InvokeBindingAsync` call with no branching; Task 1 confirmed the wire format and Task 8 exercises it end to end.

**Files:**
- Modify: `CollectDaprStats/PostgresOuput.cs`

**Interfaces:**
- Consumes: `PostgresOutput.ParseRows`, `PackageCollectionRecord` (Task 2)
- Produces: `public Task<PackageCollectionRecord[]> PostgresOutput.ReadAsync(string sqlText)`

- [x] **Step 1: Add `ReadAsync`**

Insert directly after `InsertAsync` in `CollectDaprStats/PostgresOuput.cs`:

```csharp
        public async Task<PackageCollectionRecord[]> ReadAsync(string sqlText)
        {
            var request = new BindingRequest(BindingName, QueryOperation);
            request.Metadata.Add("sql", sqlText);

            var response = await _daprClient.InvokeBindingAsync(request);

            return ParseRows(response.Data.Span);
        }
```

`BindingRequest` and `BindingResponse` come from `Dapr.Client`, which is already imported at the top of the file.

- [x] **Step 2: Verify it compiles**

Run: `dotnet build dapr-stats.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/PostgresOuput.cs
git commit -m "feat: add PostgresOutput.ReadAsync using the binding query operation"
```

---

### Task 4: `PackageDataChecker`

**Files:**
- Create: `CollectDaprStats/PackageDataChecker.cs`
- Create: `CollectDaprStats.Tests/PackageDataCheckerTests.cs`

**Interfaces:**
- Consumes: `PostgresOutput.ReadAsync`, `PackageCollectionRecord` (Tasks 2–3)
- Produces:
  - `public Task<string[]> PackageDataChecker.FindMissingAsync(string viewName, IEnumerable<string> expectedPackageNames, DateOnly collectionDate)`
  - `internal static string[] PackageDataChecker.FindMissing(PackageCollectionRecord[] rows, IEnumerable<string> expectedPackageNames, DateOnly collectionDate)`
  - `internal static DateOnly PackageDataChecker.ToCollectionDate(DateTime collectionDate)`

- [x] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/PackageDataCheckerTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class PackageDataCheckerTests
{
    private static readonly DateOnly Today = new(2026, 7, 27);

    private static PackageCollectionRecord Row(string name, DateOnly? date = null) =>
        new(name, date ?? Today);

    [Fact]
    public void FindMissing_AllPackagesStored_ReturnsEmpty()
    {
        var rows = new[] { Row("dapr"), Row("dapr-agents") };

        var missing = PackageDataChecker.FindMissing(rows, ["dapr", "dapr-agents"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_SomePackagesStored_ReturnsOnlyTheMissingOnes()
    {
        var rows = new[] { Row("dapr") };

        var missing = PackageDataChecker.FindMissing(
            rows, ["dapr", "dapr-agents", "dapr-ext-workflow"], Today);

        Assert.Equal(new[] { "dapr-agents", "dapr-ext-workflow" }, missing);
    }

    [Fact]
    public void FindMissing_ViewEmpty_ReturnsAllExpected()
    {
        var missing = PackageDataChecker.FindMissing([], ["dapr", "dapr-agents"], Today);

        Assert.Equal(new[] { "dapr", "dapr-agents" }, missing);
    }

    [Fact]
    public void FindMissing_RowsFromAnEarlierCollectionDate_ReturnsAllExpected()
    {
        // The views select MAX(collection_date), so a run that stored nothing today
        // still returns yesterday's rows. Those must not count as stored.
        var rows = new[] { Row("dapr", Today.AddDays(-1)), Row("dapr-agents", Today.AddDays(-1)) };

        var missing = PackageDataChecker.FindMissing(rows, ["dapr", "dapr-agents"], Today);

        Assert.Equal(new[] { "dapr", "dapr-agents" }, missing);
    }

    [Fact]
    public void FindMissing_StoredNameDiffersOnlyByCase_CountsAsStored()
    {
        var rows = new[] { Row("dapr.client") };

        var missing = PackageDataChecker.FindMissing(rows, ["Dapr.Client"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_DuplicateRowsForSamePackage_CountsAsStored()
    {
        var rows = new[] { Row("@dapr/dapr"), Row("@dapr/dapr") };

        var missing = PackageDataChecker.FindMissing(rows, ["@dapr/dapr"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_NothingExpected_ReturnsEmpty()
    {
        var rows = new[] { Row("dapr") };

        var missing = PackageDataChecker.FindMissing(rows, [], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void ToCollectionDate_UtcInput_KeepsCalendarDate()
    {
        var utc = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 7, 27), PackageDataChecker.ToCollectionDate(utc));
    }

    [Fact]
    public void ToCollectionDate_UnspecifiedKind_IsTreatedAsUtc()
    {
        // JSON without a trailing Z deserializes to DateTimeKind.Unspecified.
        // Calling ToUniversalTime on it would treat it as local and can shift the day.
        var unspecified = new DateTime(2026, 7, 27, 1, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateOnly(2026, 7, 27), PackageDataChecker.ToCollectionDate(unspecified));
    }
}
```

- [x] **Step 2: Run the tests to verify they fail** *(done in an earlier session; not re-verifiable now that the code exists)*

Run: `dotnet test CollectDaprStats.Tests/CollectDaprStats.Tests.csproj`
Expected: compile error — `PackageDataChecker` does not exist.

- [x] **Step 3: Implement `PackageDataChecker`**

Create `CollectDaprStats/PackageDataChecker.cs`:

```csharp
namespace DaprStats
{
    public class PackageDataChecker
    {
        private readonly PostgresOutput _output;

        public PackageDataChecker(PostgresOutput output)
        {
            _output = output;
        }

        public async Task<string[]> FindMissingAsync(
            string viewName,
            IEnumerable<string> expectedPackageNames,
            DateOnly collectionDate)
        {
            var rows = await _output.ReadAsync($"select package_name, collection_date from {viewName}");

            return FindMissing(rows, expectedPackageNames, collectionDate);
        }

        internal static string[] FindMissing(
            PackageCollectionRecord[] rows,
            IEnumerable<string> expectedPackageNames,
            DateOnly collectionDate)
        {
            var stored = rows
                .Where(r => r.CollectionDate == collectionDate)
                .Select(r => r.PackageName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return expectedPackageNames.Where(name => !stored.Contains(name)).ToArray();
        }

        internal static DateOnly ToCollectionDate(DateTime collectionDate)
        {
            var utc = collectionDate.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(collectionDate, DateTimeKind.Utc)
                : collectionDate.ToUniversalTime();

            return DateOnly.FromDateTime(utc);
        }
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CollectDaprStats.Tests/CollectDaprStats.Tests.csproj`
Expected: PASS, 16 tests (7 from Task 2 plus 9 here).

- [ ] **Step 5: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/PackageDataChecker.cs CollectDaprStats.Tests/PackageDataCheckerTests.cs
git commit -m "feat: add PackageDataChecker to diff a latest-packages view against expected names"
```

---

### Task 5: The three Check activities

Three near-identical activities. The code is written out in full for each rather than cross-referenced, because tasks may be read out of order.

**Files:**
- Create: `CollectDaprStats/CheckPythonPackageData.cs`
- Create: `CollectDaprStats/CheckNuGetPackageData.cs`
- Create: `CollectDaprStats/CheckNpmPackageData.cs`
- Modify: `CollectDaprStats/Program.cs`

**Interfaces:**
- Consumes: `PackageDataChecker.FindMissingAsync`, `PackageDataChecker.ToCollectionDate` (Task 4); the existing `PythonPackageInput(string PackageName, bool SkipStorage)`, `NuGetPackageInput(string PackageName, bool SkipStorage)`, `NpmPackageInput(string PackageName, bool SkipStorage)`
- Produces, all in namespace `DaprStats`:
  - `CheckPythonPackageData : WorkflowActivity<CheckPythonPackageDataInput, string[]>` with `record CheckPythonPackageDataInput(PythonPackageInput[] Packages, DateTime CollectionDate)`
  - `CheckNuGetPackageData : WorkflowActivity<CheckNuGetPackageDataInput, string[]>` with `record CheckNuGetPackageDataInput(NuGetPackageInput[] Packages, DateTime CollectionDate)`
  - `CheckNpmPackageData : WorkflowActivity<CheckNpmPackageDataInput, string[]>` with `record CheckNpmPackageDataInput(NpmPackageInput[] Packages, DateTime CollectionDate)`
  - Each returns the requested package names that have no row for `CollectionDate`.

Note: `NpmPackageInput` is declared outside the `DaprStats` namespace in `GetNpmPackageData.cs` (global namespace). Leave that as-is; `CheckNpmPackageData.cs` can reference it from inside `namespace DaprStats` without a using directive.

- [x] **Step 1: Create `CheckPythonPackageData`**

Create `CollectDaprStats/CheckPythonPackageData.cs`:

```csharp
using Dapr.Workflow;

namespace DaprStats
{
    public class CheckPythonPackageData : WorkflowActivity<CheckPythonPackageDataInput, string[]>
    {
        private const string ViewName = "python_dapr_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckPythonPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckPythonPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"Python packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"Python packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckPythonPackageDataInput(PythonPackageInput[] Packages, DateTime CollectionDate);
}
```

- [x] **Step 2: Create `CheckNuGetPackageData`**

Create `CollectDaprStats/CheckNuGetPackageData.cs`:

```csharp
using Dapr.Workflow;

namespace DaprStats
{
    public class CheckNuGetPackageData : WorkflowActivity<CheckNuGetPackageDataInput, string[]>
    {
        private const string ViewName = "nuget_dapr_client_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckNuGetPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckNuGetPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"NuGet packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"NuGet packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckNuGetPackageDataInput(NuGetPackageInput[] Packages, DateTime CollectionDate);
}
```

- [x] **Step 3: Create `CheckNpmPackageData`**

Create `CollectDaprStats/CheckNpmPackageData.cs`:

```csharp
using Dapr.Workflow;

namespace DaprStats
{
    public class CheckNpmPackageData : WorkflowActivity<CheckNpmPackageDataInput, string[]>
    {
        private const string ViewName = "npm_dapr_dapr_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckNpmPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckNpmPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"NPM packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"NPM packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckNpmPackageDataInput(NpmPackageInput[] Packages, DateTime CollectionDate);
}
```

- [x] **Step 4: Register the activities and the checker**

In `CollectDaprStats/Program.cs`, add the singleton next to the existing `PostgresOutput` registration:

```csharp
builder.Services.AddSingleton<PostgresOutput>();
builder.Services.AddSingleton<PackageDataChecker>();
```

And add the three activities inside the `AddDaprWorkflow` options block, after `RegisterActivity<GetPythonPackageData>()`:

```csharp
    options.RegisterActivity<CheckNuGetPackageData>();
    options.RegisterActivity<CheckNpmPackageData>();
    options.RegisterActivity<CheckPythonPackageData>();
```

- [x] **Step 5: Verify the solution builds and tests still pass**

Run: `dotnet build dapr-stats.sln`
Expected: Build succeeded, 0 errors.

Run: `dotnet test dapr-stats.sln`
Expected: PASS, 16 tests.

- [ ] **Step 6: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/CheckPythonPackageData.cs CollectDaprStats/CheckNuGetPackageData.cs CollectDaprStats/CheckNpmPackageData.cs CollectDaprStats/Program.cs
git commit -m "feat: add Check{Python,NuGet,Npm}PackageData activities"
```

---

### Task 6: `CollectorWorkflow` retry loops

**Files:**
- Modify: `CollectDaprStats/CollectorWorkflow.cs:11-45` (the three sequential package blocks)

**Interfaces:**
- Consumes: `CheckPythonPackageDataInput`, `CheckNuGetPackageDataInput`, `CheckNpmPackageDataInput` and their activities (Task 5); the existing `GetPythonPackageData`, `GetNuGetPackageData`, `GetNpmPackageData` activities
- Produces: no new public surface. `CollectorWorkflowInput` is unchanged.

There is no unit test for this task. Testing a Dapr workflow's replay behaviour needs a test harness the repo does not have, and adding one is out of scope. Verification is `dotnet build` here plus the end-to-end run in Task 8.

- [x] **Step 1: Replace the three package blocks with parallel loops**

In `CollectDaprStats/CollectorWorkflow.cs`, delete the three `if (input.NuGetPackageNames.Length > 0) { … }`, `if (input.NpmPackageNames.Length > 0) { … }`, and `if (input.PythonPackageNames.Length > 0) { … }` blocks (lines 11–45) and put this in their place, as the first thing `RunAsync` does:

```csharp
            var packageLoops = new List<Task>();

            if (input.NuGetPackageNames.Length > 0)
            {
                packageLoops.Add(CollectNuGetPackagesAsync(context, input));
            }

            if (input.NpmPackageNames.Length > 0)
            {
                packageLoops.Add(CollectNpmPackagesAsync(context, input));
            }

            if (input.PythonPackageNames.Length > 0)
            {
                packageLoops.Add(CollectPythonPackagesAsync(context, input));
            }

            if (packageLoops.Count > 0)
            {
                await Task.WhenAll(packageLoops);
            }
```

Everything after it — the DockerHub, Discord, Diagrid, and GitHub blocks, and `return true;` — stays exactly as it is.

- [x] **Step 2: Add the constants**

Add these as the first members of the `CollectorWorkflow` class, above `RunAsync`:

```csharp
        // The Get -> Check -> Sleep loop body runs at most this many times:
        // the initial pass plus two retries, so at most two backoff sleeps.
        private const int MaxAttempts = 3;

        // Long enough for the upstream package APIs to clear a 429.
        private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);
```

- [x] **Step 3: Add the three loop methods**

Add these below `RunAsync`, still inside the `CollectorWorkflow` class:

```csharp
        private static async Task CollectNuGetPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.NuGetPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetNuGetPackageData),
                        new NuGetPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(5), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckNuGetPackageData),
                    new CheckNuGetPackageDataInput(
                        pending.Select(p => new NuGetPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
        }

        private static async Task CollectNpmPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.NpmPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetNpmPackageData),
                        new NpmPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(5), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckNpmPackageData),
                    new CheckNpmPackageDataInput(
                        pending.Select(p => new NpmPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
        }

        private static async Task CollectPythonPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.PythonPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetPythonPackageData),
                        new PythonPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(10), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckPythonPackageData),
                    new CheckPythonPackageDataInput(
                        pending.Select(p => new PythonPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
        }
```

- [x] **Step 4: Re-read the file and check the determinism rules** *(verified 2026-07-28: all three checks pass)*

Read the whole of `CollectDaprStats/CollectorWorkflow.cs` and confirm:
- Every `await` in the three new methods is on `context.CallActivityAsync` or `context.CreateTimer`.
- No `DateTime.Now`, `DateTime.UtcNow`, `Task.Delay`, `Task.Run`, `Guid.NewGuid`, `Random`, or HTTP call appears anywhere in the file.
- `RateLimitBackoff` is `static readonly`, computed from a literal, not from the clock.

- [x] **Step 5: Verify the solution builds and tests still pass**

Run: `dotnet build dapr-stats.sln`
Expected: Build succeeded, 0 errors.

Run: `dotnet test dapr-stats.sln`
Expected: PASS, 16 tests.

- [ ] **Step 6: Commit** *(agents: ask first)*

```bash
git add CollectDaprStats/CollectorWorkflow.cs
git commit -m "feat: retry package collection for packages missing from Postgres"
```

---

### Task 7: CI timeout and polling

Without this the scheduled job is killed at 10 minutes and the retry loops never get to run.

**Files:**
- Modify: `.github/workflows/run-workflow.yaml:18` (`timeout-minutes`)
- Modify: `.github/workflows/run-workflow.yaml:92-110` (the wait and status-check steps)

**Interfaces:**
- Consumes: `steps.start-workflow.outputs.instance_id`, already produced by the existing `Generate Variables and Start CollectorWorkflow` step
- Produces: `steps.check-status.outputs.status`, consumed unchanged by the existing `Evaluate Result` step

- [x] **Step 1: Raise the job timeout**

In `.github/workflows/run-workflow.yaml`, change:

```yaml
    timeout-minutes: 10
```

to:

```yaml
    timeout-minutes: 30
```

30 minutes covers the ~12 minute worst-case package phase plus the GitHub child workflow, with headroom.

- [x] **Step 2: Replace the fixed sleep and the status check with one polling step**

Delete both the `Wait for Workflow Completion` step (the one running `sleep 180`) and the `Check Workflow Status` step, and put this single step in their place:

```yaml
      - name: Wait for Workflow Completion
        id: check-status
        run: |
          INSTANCE_ID="${{ steps.start-workflow.outputs.instance_id }}"
          echo "Polling status for instance: $INSTANCE_ID"

          DEADLINE=$((SECONDS + 1500))   # 25 minutes
          STATUS="UNKNOWN"

          while [ $SECONDS -lt $DEADLINE ]; do
            STATUS=$(curl -s "http://localhost:3500/v1.0/workflows/dapr/$INSTANCE_ID" | jq -r '.runtimeStatus')
            echo "Workflow status: $STATUS"
            case "$STATUS" in
              COMPLETED|FAILED|TERMINATED) break ;;
            esac
            sleep 30
          done

          echo "status=$STATUS" >> $GITHUB_OUTPUT
```

The `Evaluate Result` step that follows already reads `steps.check-status.outputs.status`, so it needs no change.

- [x] **Step 3: Verify the YAML parses**

Run: `py -c "import yaml; yaml.safe_load(open('.github/workflows/run-workflow.yaml', encoding='utf-8'))" && echo OK`
Expected: `OK`

Note: on this machine the interpreter is `py`, not `python`, and `encoding='utf-8'` is required — the file contains ✅/❌ in the `Evaluate Result` step, which the default cp1252 codec cannot decode.

If Python is unavailable, read the file back and confirm the new step sits at the same indentation as its siblings (six spaces before `- name:`) and that `Evaluate Result` still follows it.

- [ ] **Step 4: Commit** *(agents: ask first)*

```bash
git add .github/workflows/run-workflow.yaml
git commit -m "ci: poll for workflow completion and raise the job timeout to 30 minutes"
```

---

### Task 8: End-to-end verification

**Files:**
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: everything from Tasks 2–7
- Produces: nothing. This is the acceptance gate.

- [x] **Step 1: Add single-ecosystem requests to `local-tests.http`**

Append to the end of the file:

```http
###
### Python packages only, with storage. Exercises the
### GetPythonPackageData -> CheckPythonPackageData -> Sleep loop.
###
// @name pythononly
POST {{dapr_url}}/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
Content-Type: application/json

{
    "CollectionDate" : "{{currentDate}}",
    "NuGetPackageNames" : [],
    "NpmPackageNames" : [],
    "PythonPackageNames" : ["dapr", "dapr-agents", "dapr-ext-workflow"],
    "CollectDiscordData" : false,
    "CollectGitHubData" : false,
    "CollectDiagridDashboardData" : false,
    "DockerHubImages" : [],
    "SkipStorage": false
}

###
### Python packages with SkipStorage. The workflow must skip the check entirely,
### so this completes in seconds with no 5-minute sleep.
### Note: GetPythonPackageData ignores SkipStorage and inserts anyway (a
### pre-existing quirk that npm and NuGet do not share), so rows are still
### written. What is being verified here is that the check is skipped.
###
// @name pythonskipstorage
POST {{dapr_url}}/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
Content-Type: application/json

{
    "CollectionDate" : "{{currentDate}}",
    "NuGetPackageNames" : [],
    "NpmPackageNames" : [],
    "PythonPackageNames" : ["dapr", "dapr-agents", "dapr-ext-workflow"],
    "CollectDiscordData" : false,
    "CollectGitHubData" : false,
    "CollectDiagridDashboardData" : false,
    "DockerHubImages" : [],
    "SkipStorage": true
}

###
### A package name that does not exist upstream. GetPythonPackageData returns
### false without inserting, so the check reports it missing on all three passes
### and the loop exits after two 5-minute sleeps (~11 minutes total).
###
// @name pythonmissing
POST {{dapr_url}}/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
Content-Type: application/json

{
    "CollectionDate" : "{{currentDate}}",
    "NuGetPackageNames" : [],
    "NpmPackageNames" : [],
    "PythonPackageNames" : ["dapr", "this-package-does-not-exist-xyz"],
    "CollectDiscordData" : false,
    "CollectGitHubData" : false,
    "CollectDiagridDashboardData" : false,
    "DockerHubImages" : [],
    "SkipStorage": false
}
```

The third request uses `SkipStorage: false` because the check only runs when storage is enabled, and that is the whole point of the request. It writes a real `dapr` row for today, which is harmless — the collection is idempotent by date at the view level.

- [x] **Step 2: Verify the SkipStorage short-circuit** — **PASSED, 2026-07-28** (instance `skipstorage-131421`). `COMPLETED` in 31s (13:14:21.5 → 13:14:52.5), no 5-minute pause. Zero `Python packages…` lines from `CheckPythonPackageData`, so the `if (input.SkipStorage) return;` guard is in the right place.

Incidental but significant: `dapr-agents` received an **HTTP 429 from pypistats.org** during this run — the very failure mode this feature exists to handle, hit on the first attempt. `GetPythonPackageData` returned `false` silently (no log line, no exception, no insert; see `GetPythonPackageData.cs:46`), and because `SkipStorage` was true nothing detected it. That silence is the status quo the Check activities replace.

Run: `dapr run -f .`

Send the `pythonskipstorage` request, then poll `GET {{dapr_url}}/v1.0/workflows/dapr/{{instanceID}}`.

Expected: `COMPLETED` within about 40 seconds (three packages × 10s spacing). The console shows three `Python Package: …` lines and **no** `Python packages…` line from `CheckPythonPackageData`, because the check is skipped.

If a `Python packages…` line does appear, the `if (input.SkipStorage) return;` guard in `CollectPythonPackagesAsync` is missing or misplaced.

- [x] **Step 3: Verify the happy path** — **PASSED, 2026-07-28** (instance `pythononly-132118`), though not via the clean single-pass route the step anticipated. A real pypistats 429 on `dapr-agents` turned this into a live end-to-end demonstration of the retry feature:

```
Pass 1:  dapr OK | dapr-agents -> 429 | dapr-ext-workflow OK
Check:   Python packages not stored for 2026-07-28: dapr-agents
         -- 5 minute backoff --
Pass 2:  Python Package: dapr-agents, Downloads: 12402
Check:   Python packages: all 1 stored for 2026-07-28
COMPLETED in 5m42s (13:21:18 -> 13:27:00)
```

The step's stated expectation (~40s, `all 3 stored`, no pause) did **not** hold, but its failure criterion — packages *you watched succeed* being reported missing — did not occur either. `dapr-agents` genuinely failed, so reporting it missing was correct. What this proves: `ReadAsync` works against the real binding, the date comparison counts today's rows correctly, only the pending package is retried (`all 1 stored`, and a single `Python Package:` line in pass 2), the 5-minute backoff fires, and the loop exits once nothing is pending.

Send the `pythononly` request and poll for status.

Expected: `COMPLETED` in roughly 40 seconds. The console shows three `Python Package: …` lines followed by exactly one `Python packages: all 3 stored for <today>` line, and no 5-minute pause. This proves `ReadAsync` works against the real binding and that the loop exits on the first pass.

If instead you see `Python packages not stored for …` listing packages you just watched succeed, the read path or the date comparison is wrong — do not proceed; debug `ParseRows`/`FindMissing` against the raw response from Task 1.

- [x] **Step 4: Verify the retry loop actually retries** — **PASSED, 2026-07-28** (instance `pythonmissing-132741`). `COMPLETED` in 10m41.9s (13:27:41.9 → 13:38:23.9), matching two 5-minute backoffs plus overhead.

| Assertion | Expected | Observed |
|---|---|---|
| Collection rounds | 3 | 3 `not stored` lines |
| Fetch attempts for the fake package | 3 | 3 |
| `dapr` re-fetched after round 1 | no | exactly 1 `Python Package: dapr,` line in the whole run |
| Gap after the third check | none | workflow completed immediately |

`MaxAttempts` bounds the loop rather than retrying forever, and only the pending package is carried into later rounds. Incidental: round 2 got a 429 on the nonexistent package while rounds 1 and 3 got 404 — pypistats was rate-limiting steadily throughout this session.

Send the `pythonmissing` request and poll. This one takes ~11 minutes; that is the two backoff sleeps doing their job.

Expected: `COMPLETED` after about 11 minutes. The console shows three rounds of collection, each followed by `Python packages not stored for <today>: this-package-does-not-exist-xyz`, with a 5-minute gap between rounds and **no** gap after the third. The second and third rounds collect only `this-package-does-not-exist-xyz` — `dapr` is not re-fetched. That last point is the whole feature.

- [x] **Step 5: Verify the full workflow still works** — **PASSED, 2026-07-28** (instance `allthree-135147`), run with a narrowed scope by agreement with the user: all three package ecosystems, but `DockerHubImages: []` and Discord/GitHub/Diagrid left `false`. Rationale: Task 6 only changed the package loops and their parallelism; the DockerHub, Discord, Diagrid, and GitHub blocks are byte-for-byte unchanged, and including them would have required three more secrets and a long GitHub child-workflow traversal.

`COMPLETED` in 48.2s (13:51:47.8 → 13:52:35.9), all three checks clean on the first pass:

```
NPM packages:    all 1 stored for 2026-07-28
Python packages: all 3 stored for 2026-07-28
NuGet packages:  all 8 stored for 2026-07-28
```

Log lines interleave across the three ecosystems throughout, confirming `Task.WhenAll` parallelism (expected, not a bug). 48s for all three beats the ~75s the old sequential version needed for its timers alone. The 8-package NuGet check returned `all 8 stored`, so the deferred name-drift risk did not materialise.

**Not covered by this run:** the DockerHub, Discord, Diagrid, and GitHub child-workflow blocks were not executed. They are unchanged code, but if you want the literal full-request verification, send the original `Start a complete CollectorWorkflow` request with all four secrets set.

Send the original `Start a complete CollectorWorkflow` request and poll.

Expected: `COMPLETED`. The three ecosystems' log lines interleave now that the loops run in parallel — that is expected, not a bug.

- [x] **Step 6: Stop the app and run the full test suite** — **PASSED, 2026-07-28.** `dapr stop -f .` reported "Dapr and app processes stopped successfully"; `dotnet test dapr-stats.sln` → 16 passed, 0 failed, 0 skipped. Stopping first is required on Windows, otherwise the build fails on a file lock against the running app's `CollectDaprStats.dll`.

Run: `dapr stop -f .`
Run: `dotnet test dapr-stats.sln`
Expected: PASS, 16 tests.

- [ ] **Step 7: Commit** *(agents: ask first)*

```bash
git add local-tests.http
git commit -m "test: add single-ecosystem and SkipStorage workflow requests"
```

---

## Deferred, with reasons

These are named in the spec's Known limitations and are deliberately not in any task:

- **Midnight-UTC date mismatch.** `Get*PackageData` inserts `DateTime.UtcNow` while the checks compare against `input.CollectionDate`. A run straddling midnight UTC would report packages as permanently missing. The cron fires at 09:00 UTC and the loop window is ~12 minutes, so this cannot trigger on the schedule.
- **NuGet name drift.** `GetNuGetPackageData` stores the top `SearchAsync` hit's `Identity.Id`, which may not equal the requested name. If it ever differs, that package is reported missing on every pass. `MaxAttempts` bounds the cost and the check's log line makes the cause visible.
- **`GetPythonPackageData` ignores `SkipStorage`.** Unlike `GetNpmPackageData` and `GetNuGetPackageData`, it has no `if (!input.SkipStorage)` guard and always inserts. Found while writing Task 8. It does not affect this feature — the workflow's own `SkipStorage` guard skips the check regardless — but it is a genuine bug worth a separate one-line fix.
