# Scarf Java Package Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collect weekly per-version download counts for all 18 `io.dapr` JVM packages from Scarf, replacing a manual Sonatype CSV import that stopped in April 2025.

**Architecture:** One new activity, `GetJavaPackageData`, makes a single Scarf v3 export request (`query=io.dapr*`, `rollup=weekly`, `breakdown_set=by-version`) covering every package, and upserts one row per (week, package, version) into a new `java_dapr` table shaped like `npm_dapr_dapr`. It reuses the existing `ScarfExportClient` and joins the concurrent package group in `CollectorWorkflow`, adding no wall-clock time. To fit the new input, `run-workflow.yaml`'s three package inputs merge into one, taking it from GitHub's 10-input cap down to 8.

**Tech Stack:** .NET 10, Dapr Workflow, xUnit, Neon Postgres 16 via the `daprstats` Dapr Postgres binding.

**Spec:** [docs/superpowers/specs/2026-09-15-scarf-java-package-collection-design.md](../specs/2026-09-15-scarf-java-package-collection-design.md)

## Global Constraints

- **.NET 10.** Build with `dotnet build dapr-stats.sln`, test with `dotnet test dapr-stats.sln`. The suite is 207 pure unit tests running in ~100 ms; run it on every task.
- **Workflows and activities are never registered explicitly.** A new `WorkflowActivity<,>` subclass is discovered automatically. Only constructor-injected dependencies need `Program.cs` — and `ScarfExportClient` and `PostgresOutput` are both already registered singletons, so **`Program.cs` is not modified by this plan.**
- **`CollectorWorkflow.RunAsync` must stay deterministic.** No `DateTime.Now`, no `Guid.NewGuid()`, no HTTP. All I/O lives in activities. Use `context.CreateTimer`, never `Task.Delay`.
- **Every `InsertAsync` is guarded by `if (!input.SkipStorage)`.** This is what makes dry runs safe.
- **An empty list means "skip this source."** Guard with `?.Length > 0` where a payload field may be omitted entirely.
- **`workflow_dispatch` allows at most 10 inputs.** `run-workflow.yaml` is at the cap.
- **Never apply DDL without asking.** Dry-run migrations inside an aborting `DO $$ ... RAISE EXCEPTION 'DRY RUN OK - rolling back'; END $$;` block. `P0001 DRY RUN OK` means every statement succeeded.
- **Never print or copy a secret.** `CollectDaprStats/secrets.json` is gitignored; `SCARF_DAPR_API_TOKEN` comes from the environment.
- **Git commits on `feat/scarf-java-package-collection` are authorised** for this work. Do not push, merge, rebase, or branch further without asking.
- **Exact table name:** `java_dapr`. **Exact constraint name:** `java_dapr_week_unique`.
- **Exact default query pattern:** `io.dapr*`.

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `CollectDaprStats/ScarfExportClient.cs` | Modify | Add the `Query` field, emit `&query=`, reject `Query` + `PixelIds` together, add `FetchPackageVersionsAsync` |
| `CollectDaprStats/ScarfAggregationResponse.cs` | Modify | Add `PackageRow` and `ParsePackageVersions`; extract the shared envelope reader |
| `CollectDaprStats/GetJavaPackageData.cs` | Create | The activity: pick the week, fetch, chunk, upsert |
| `CollectDaprStats/CollectorWorkflow.cs` | Modify | `JavaPackageNames` on the input record, guarded call site |
| `postgres/add-java-dapr-2026-09-15.psql` | Create | Dated migration, dry-run first |
| `postgres/postgres_schema.psql` | Modify | Add `java_dapr` |
| `.github/workflows/run-workflow.yaml` | Modify | Merge three package inputs into one, add `DEFAULT_JAVA_PACKAGES` |
| `local-tests.http` | Modify | `JavaPackageNames` in all three payloads |
| `README.md`, `AGENTS.md`, `postgres/java-import.md` | Modify | Document the source, the traps, and the frozen archive |
| `CollectDaprStats.Tests/ScarfExportClientTests.cs` | Modify | URL shape for the query DSL |
| `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs` | Modify | `ParsePackageVersions` + a `Parse` regression |
| `CollectDaprStats.Tests/CollectorInsertSqlTests.cs` | Modify | Golden string for the chunked upsert |
| `CollectDaprStats.Tests/IsoWeekTests.cs` | Modify | `CompleteWeeksBefore(x, 2)[0]` is the week before last |

---

### Task 1: Scarf client — the package-name query DSL

**Files:**
- Modify: `CollectDaprStats/ScarfExportClient.cs`
- Test: `CollectDaprStats.Tests/ScarfExportClientTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `ScarfExportRequest` gains a ninth positional parameter `string? Query = null`. The default keeps all three existing call sites and both existing golden tests compiling unchanged.
  - `ScarfExportClient.FetchPackageVersionsAsync(ScarfExportRequest) → Task<IReadOnlyList<ScarfAggregationResponse.PackageRow>>` — added in Task 2, once `PackageRow` exists. This task adds only the record field and `BuildUrl` support.

- [ ] **Step 1: Write the failing tests**

Add to `CollectDaprStats.Tests/ScarfExportClientTests.cs`:

```csharp
    [Fact]
    public void BuildUrl_JavaPackageShape_SendsTheQueryDslRaw()
    {
        // The exact form verified against the live API on 2026-09-15: the `*`
        // is NOT percent-encoded. Same reasoning as the unescaped comma in
        // breakdown_set — this is a verified request, not a guess.
        var request = new ScarfExportRequest(
            "Dapr",
            "SCARF_DAPR_API_TOKEN",
            [],
            From,
            To,
            Breakdown: null,
            BreakdownSet: "by-version",
            GroupByArtifact: null,
            Query: "io.dapr*");

        Assert.Equal(
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export" +
            "?start_date=2026-08-10" +
            "&end_date=2026-08-31" +
            "&query=io.dapr*" +
            "&rollup=weekly" +
            "&breakdown_set=by-version" +
            "&format=json",
            ScarfExportClient.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_QueryWithPixelIds_Throws()
    {
        // Scarf's v3 spec: the package-name query combines with neither
        // package_id nor tracking_pixel_id. Failing here beats a runtime 422.
        var request = new ScarfExportRequest(
            "Dapr",
            "SCARF_DAPR_API_TOKEN",
            [DaprDocsPixel],
            From,
            To,
            Breakdown: null,
            BreakdownSet: "by-version",
            GroupByArtifact: null,
            Query: "io.dapr*");

        var ex = Assert.Throws<ArgumentException>(
            () => ScarfExportClient.BuildUrl(request));
        Assert.Contains("query", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~ScarfExportClientTests"`

Expected: FAIL to compile — `ScarfExportRequest` does not accept a `Query` argument.

- [ ] **Step 3: Add the field and the URL support**

In `CollectDaprStats/ScarfExportClient.cs`, add the ninth parameter to the record:

```csharp
    public sealed record ScarfExportRequest(
        string Owner,            // "Dapr" | "Diagrid" — case-sensitive.
        string ApiTokenSecret,   // "SCARF_DAPR_API_TOKEN" | "SCARF_DIAGRID_API_TOKEN"
        string[] PixelIds,
        DateOnly From,
        DateOnly To,             // Exclusive, verified 2026-09-01.
        string? Breakdown,       // "by-company", or null to omit.
        string? BreakdownSet,    // "by-referer,by-company", or null to omit.
        bool? GroupByArtifact,   // null omits the parameter entirely.
        string? Query = null);   // package-name DSL; excludes PixelIds.
```

At the very top of `BuildUrl`, before the `StringBuilder` is created:

```csharp
            if (request.Query is not null && request.PixelIds.Length > 0)
            {
                throw new ArgumentException(
                    "Scarf rejects the package-name query together with a " +
                    "tracking_pixel_id or package_id selector. Set one or the " +
                    "other, not both.",
                    nameof(request));
            }
```

Immediately after the `foreach` loop that appends `tracking_pixel_id`, and before `rollup`:

```csharp
            if (request.Query is { } query)
            {
                // Appended raw, like the comma in breakdown_set below: this is
                // the exact form verified against the live API on 2026-09-15.
                // The DSL's metacharacters are `*`, `{`, `}` and `,`, none of
                // which survive Uri.EscapeDataString intact.
                url.Append("&query=").Append(query);
            }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~ScarfExportClientTests"`

Expected: PASS, including the two pre-existing `IsUnchangedByTheExtraction` golden-string tests — they must not have moved.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, 209 tests.

- [ ] **Step 6: Commit**

```bash
git add CollectDaprStats/ScarfExportClient.cs CollectDaprStats.Tests/ScarfExportClientTests.cs
git commit -m "feat: support Scarf's package-name query DSL in ScarfExportClient

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Parse by-version package rows

**Files:**
- Modify: `CollectDaprStats/ScarfAggregationResponse.cs`
- Modify: `CollectDaprStats/ScarfExportClient.cs`
- Test: `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs`

**Interfaces:**
- Consumes: `ScarfExportRequest.Query` from Task 1.
- Produces:
  - `ScarfAggregationResponse.PackageRow(DateOnly WeekStart, string PackageName, string Version, long Downloads, long UniqueOrigins)`
  - `ScarfAggregationResponse.ParsePackageVersions(ReadOnlySpan<byte>) → IReadOnlyList<PackageRow>`
  - `ScarfExportClient.FetchPackageVersionsAsync(ScarfExportRequest) → Task<IReadOnlyList<ScarfAggregationResponse.PackageRow>>`

**Why a second entry point rather than reusing `Parse`:** `Parse` skips every row with a null `company_name`. A `by-version` row has no company, so `Parse` would return zero rows from a perfectly healthy response.

- [ ] **Step 1: Write the failing tests**

Add to `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs`:

```csharp
    private static IReadOnlyList<ScarfAggregationResponse.PackageRow> ParsePackages(
        string json) =>
        ScarfAggregationResponse.ParsePackageVersions(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParsePackageVersions_ByVersionRow_ReadsThePackageAndVersion()
    {
        // A real row from the 2026-09-15 probe, company fields null as they
        // always are for a by-version breakdown.
        var rows = ParsePackages("""
        {"data":[{
          "date":"2026-08-24","artifact":"0d1e80df-9a30-4b62-92e0-3814ce4bcffa",
          "artifact_name":"io.dapr.spring/dapr-spring-boot-autoconfigure",
          "artifact_type":"package","rollup":"weekly","breakdown":"by-version",
          "breakdowns":["by-version"],
          "country":null,"company_name":null,"company_domain":null,
          "referer":null,"version":"1.16.1-rc-3","points":2500,
          "company_sic_codes":[],"company_count":3,
          "last_seen":"2026-08-30 00:00:00+00:00",
          "total":6,"unique_origins":3,"unique_endpoints":1
        }]}
        """);

        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 8, 24), row.WeekStart);
        Assert.Equal("io.dapr.spring/dapr-spring-boot-autoconfigure", row.PackageName);
        Assert.Equal("1.16.1-rc-3", row.Version);
        Assert.Equal(6, row.Downloads);
        Assert.Equal(3, row.UniqueOrigins);
    }

    [Fact]
    public void ParsePackageVersions_RowMissingArtifactNameOrVersion_IsSkipped()
    {
        // Defensive: never observed on a probe, but both reach NOT NULL key
        // columns.
        var rows = ParsePackages("""
        {"data":[
          {"date":"2026-08-24","artifact_name":null,"version":"1.16.1",
           "total":5,"unique_origins":2},
          {"date":"2026-08-24","artifact_name":"io.dapr/dapr-sdk","version":null,
           "total":5,"unique_origins":2},
          {"date":"2026-08-24","artifact_name":"io.dapr/dapr-sdk","version":"1.16.1",
           "total":5,"unique_origins":2}
        ]}
        """);

        var row = Assert.Single(rows);
        Assert.Equal("io.dapr/dapr-sdk", row.PackageName);
    }

    [Fact]
    public void ParsePackageVersions_MultipleWeeks_KeepsEachRowsOwnWeek()
    {
        var rows = ParsePackages("""
        {"data":[
          {"date":"2026-08-24","artifact_name":"io.dapr/dapr-sdk","version":"1.16.1",
           "total":10,"unique_origins":4},
          {"date":"2026-08-31","artifact_name":"io.dapr/dapr-sdk","version":"1.16.1",
           "total":20,"unique_origins":7}
        ]}
        """);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 8, 24), rows[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 8, 31), rows[1].WeekStart);
    }

    [Fact]
    public void Parse_StillDropsNullCompanyRows_SoTheTwoPathsStayDistinct()
    {
        // Regression guard: if someone ever "simplifies" Parse and
        // ParsePackageVersions into one method, this fails first.
        var rows = Parse("""
        {"data":[{
          "date":"2026-08-24","artifact_name":"io.dapr/dapr-sdk",
          "breakdown":"by-version","version":"1.16.1",
          "company_name":null,"company_domain":null,"referer":null,
          "total":5,"unique_origins":2
        }]}
        """);

        Assert.Empty(rows);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~ScarfAggregationResponseTests"`

Expected: FAIL to compile — `ParsePackageVersions` does not exist.

- [ ] **Step 3: Extract the shared envelope reader**

In `CollectDaprStats/ScarfAggregationResponse.cs`, replace the envelope validation at the top of `Parse` with a call to a new private helper, leaving `Parse`'s behaviour identical. Add this method to the class:

```csharp
        /// <summary>
        /// Validates the `{"data":[...]}` envelope and returns the array.
        /// Shared so both entry points reject a malformed body identically.
        /// </summary>
        private static JsonElement ReadDataArray(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                throw new FormatException("Scarf returned an empty response body.");
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Scarf response is not valid JSON.", ex);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Scarf response is not a JSON object.");
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Scarf response has no data array.");
            }

            return data;
        }
```

Then `Parse`'s body begins:

```csharp
        public static IReadOnlyList<Row> Parse(ReadOnlySpan<byte> json)
        {
            var data = ReadDataArray(json);
            var rows = new List<Row>(data.GetArrayLength());
```

and continues unchanged from its existing `foreach`.

- [ ] **Step 4: Add the package record and parser**

Add to the same class, beside the existing `Row` record:

```csharp
        /// <summary>
        /// One row of a `breakdown_set=by-version` package export.
        /// </summary>
        public sealed record PackageRow(
            DateOnly WeekStart,
            string PackageName,
            string Version,
            long Downloads,
            long UniqueOrigins);

        /// <summary>
        /// Parses a by-version package export.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Parse"/> because that one skips rows with
        /// no `company_name`, which is every row of this breakdown.
        /// </remarks>
        public static IReadOnlyList<PackageRow> ParsePackageVersions(
            ReadOnlySpan<byte> json)
        {
            var data = ReadDataArray(json);
            var rows = new List<PackageRow>(data.GetArrayLength());

            foreach (var element in data.EnumerateArray())
            {
                var packageName = ReadString(element, "artifact_name");
                var version = ReadString(element, "version");

                // Defensive: every probe row carried both. A null here would
                // otherwise reach a NOT NULL key column.
                if (string.IsNullOrWhiteSpace(packageName) ||
                    string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                rows.Add(new PackageRow(
                    ReadWeekStart(element),
                    packageName,
                    version,
                    ReadCount(element, "total"),
                    ReadCount(element, "unique_origins")));
            }

            return rows;
        }
```

- [ ] **Step 5: Add the client fetch method**

In `CollectDaprStats/ScarfExportClient.cs`, extract the HTTP call out of `FetchAsync` into a private method and add the package variant:

```csharp
        public async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            ScarfExportRequest request) =>
            ScarfAggregationResponse.Parse(await SendAsync(request));

        public async Task<IReadOnlyList<ScarfAggregationResponse.PackageRow>>
            FetchPackageVersionsAsync(ScarfExportRequest request) =>
            ScarfAggregationResponse.ParsePackageVersions(await SendAsync(request));

        private async Task<byte[]> SendAsync(ScarfExportRequest request)
        {
            var secrets = await _daprClient.GetSecretAsync(
                SecretStore, request.ApiTokenSecret);
            var token = secrets[request.ApiTokenSecret];

            using var message = new HttpRequestMessage(
                HttpMethod.Get, BuildUrl(request));
            message.Headers.Add("Authorization", $"Bearer {token}");

            var response = await _httpClient.SendAsync(message);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Scarf returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return payload;
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, 213 tests.

- [ ] **Step 7: Commit**

```bash
git add CollectDaprStats/ScarfAggregationResponse.cs CollectDaprStats/ScarfExportClient.cs CollectDaprStats.Tests/ScarfAggregationResponseTests.cs
git commit -m "feat: parse Scarf by-version package rows

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: The `java_dapr` table

**Files:**
- Create: `postgres/add-java-dapr-2026-09-15.psql`
- Modify: `postgres/postgres_schema.psql`

**Interfaces:**
- Consumes: nothing.
- Produces: table `java_dapr` with columns `(id, package_name, collection_date, package_version, download_count, collected_over_number_of_days, week_start, unique_origins)` and constraint `java_dapr_week_unique UNIQUE (week_start, package_name, package_version)`. Task 4's SQL depends on these exact names.

**Why `week_start` and not `collection_week`:** the [dedup migration](../../../postgres/add-collector-week-dedup-2026-09-08.psql) states that `collection_week` means only "the week the run happened in", and warns it is off by about a week against `scarf_*.week_start`. Here the offset is deliberately two weeks. `week_start` is also the better conflict target — a run delayed across a week boundary (measured at 2h, 19h and once 8 days on this workflow) cannot then write a second copy of a week it already holds.

- [ ] **Step 1: Write the migration script**

Create `postgres/add-java-dapr-2026-09-15.psql`:

```sql
-- Adds java_dapr: weekly per-version JVM package downloads from Scarf.
--
-- Replaces the manual Sonatype CSV import described in java-import.md, which
-- last ran for April 2025 and only ever covered `dapr-sdk`. java_dapr_sdk is
-- left untouched as the frozen archive of that import.
--
-- week_start is "the ISO week this data describes", the sense scarf_* and
-- datadog_rum* use. It is deliberately NOT called collection_week, which in
-- the seven deduplicated tables means "the week the run happened in". Here the
-- two differ by two weeks: Scarf ingests two to three days late, so a Monday
-- run collects the week before last, not the week that ended the day before.
--
-- The unique constraint keys on week_start rather than the run's week so that
-- a delayed run cannot write a second copy of a week it already holds.

CREATE TABLE java_dapr (
    id SERIAL PRIMARY KEY,
    package_name VARCHAR(255) NOT NULL,
    collection_date TIMESTAMP NOT NULL,
    package_version VARCHAR(100) NOT NULL,
    download_count BIGINT NOT NULL,
    collected_over_number_of_days SMALLINT,
    week_start DATE NOT NULL,
    unique_origins BIGINT,
    CONSTRAINT java_dapr_week_unique
      UNIQUE (week_start, package_name, package_version)
);
```

- [ ] **Step 2: Dry-run it against the live schema**

Get the connection string, then send the `CREATE TABLE` wrapped in an aborting `DO` block so nothing persists:

```bash
CS=$(neon connection-string --project-id spring-pine-41263944 --role-name marcduiker --database-name daprstats)
HOST=$(echo "$CS" | sed -E 's|.*@([^/]+)/.*|\1|')
curl -s -X POST "https://$HOST/sql" \
  -H "Neon-Connection-String: $CS" -H "Content-Type: application/json" \
  -d '{"query":"DO $$ BEGIN CREATE TABLE java_dapr (id SERIAL PRIMARY KEY, package_name VARCHAR(255) NOT NULL, collection_date TIMESTAMP NOT NULL, package_version VARCHAR(100) NOT NULL, download_count BIGINT NOT NULL, collected_over_number_of_days SMALLINT, week_start DATE NOT NULL, unique_origins BIGINT, CONSTRAINT java_dapr_week_unique UNIQUE (week_start, package_name, package_version)); RAISE EXCEPTION $x$DRY RUN OK - rolling back$x$; END $$;","params":[]}'
```

Expected: a `P0001` error whose message is `DRY RUN OK - rolling back`. Any other SQLSTATE is a real failure — fix the script and re-run. Confirm nothing persisted:

```bash
curl -s -X POST "https://$HOST/sql" \
  -H "Neon-Connection-String: $CS" -H "Content-Type: application/json" \
  -d '{"query":"select count(*) from information_schema.tables where table_name = '"'"'java_dapr'"'"'","params":[]}'
```

Expected: `0`.

- [ ] **Step 3: STOP and ask before applying**

**Do not apply the DDL.** This is the production database. Report the dry-run result and ask the maintainer to approve applying it. Only after an explicit yes, re-send the bare `CREATE TABLE` without the `DO` wrapper, then re-run the `information_schema` check and expect `1`.

- [ ] **Step 4: Add the table to the schema file**

In `postgres/postgres_schema.psql`, immediately after the `java_dapr_sdk` block, add the same `CREATE TABLE java_dapr (...)` statement, preceded by a short comment:

```sql
-- Weekly per-version JVM downloads from Scarf, collected by GetJavaPackageData.
-- week_start is the ISO week the data describes; because Scarf ingests two to
-- three days late, it is two weeks behind collection_date, not one.
-- java_dapr_sdk above is the frozen Sonatype archive and is no longer written.
```

- [ ] **Step 5: Commit**

```bash
git add postgres/add-java-dapr-2026-09-15.psql postgres/postgres_schema.psql
git commit -m "feat: java_dapr table for weekly JVM package downloads

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: The `GetJavaPackageData` activity

**Files:**
- Create: `CollectDaprStats/GetJavaPackageData.cs`
- Test: `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`
- Test: `CollectDaprStats.Tests/IsoWeekTests.cs`

**Interfaces:**
- Consumes: `ScarfExportRequest.Query` (Task 1), `ScarfExportClient.FetchPackageVersionsAsync` and `ScarfAggregationResponse.PackageRow` (Task 2), table `java_dapr` (Task 3).
- Produces:
  - `public record JavaPackageInput(string[] PackageNames, DateTime CollectionDate, bool SkipStorage)` — consumed by Task 5's call site.
  - `GetJavaPackageData.BuildInsertSql(int rowCount) → string`, `internal`, for the golden test.

- [ ] **Step 1: Write the failing tests**

Add to `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`:

```csharp
    [Fact]
    public void Java_InsertSql_ChunksRowsAndUpsertsOnWeekStart()
    {
        // The only collector that chunks AND upserts: the Scarf collectors
        // chunk without upserting, the package collectors upsert one row at a
        // time. This asserts the two builders compose.
        Assert.Equal(
            "insert into java_dapr (package_name, collection_date, package_version, " +
            "download_count, collected_over_number_of_days, week_start, unique_origins) " +
            "values ($1,$2,$3,$4,$5,$6::date,$7),($8,$9,$10,$11,$12,$13::date,$14) " +
            "on conflict (week_start,package_name,package_version) do update set " +
            "collection_date=excluded.collection_date," +
            "download_count=excluded.download_count," +
            "unique_origins=excluded.unique_origins," +
            "collected_over_number_of_days=excluded.collected_over_number_of_days",
            GetJavaPackageData.BuildInsertSql(2));
    }
```

Add to `CollectDaprStats.Tests/IsoWeekTests.cs`:

```csharp
    [Fact]
    public void CompleteWeeksBefore_TwoWeeks_FirstIsTheWeekBeforeLast()
    {
        // What GetJavaPackageData collects. Scarf ingests two to three days
        // late, so the most recently completed week is skipped.
        // Monday 2026-09-14 08:43 UTC is a scheduled run.
        var weeks = IsoWeek.CompleteWeeksBefore(
            new DateTime(2026, 9, 14, 8, 43, 0, DateTimeKind.Utc), 2);

        Assert.Equal(new DateOnly(2026, 8, 31), weeks[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 9, 7), weeks[1].WeekStart);
    }

    [Fact]
    public void CompleteWeeksBefore_TwoWeeks_MidweekManualRunPicksTheSameWeek()
    {
        // A Wednesday re-run must target the same week as Monday's run, so the
        // upsert overwrites rather than adding a second week.
        var weeks = IsoWeek.CompleteWeeksBefore(
            new DateTime(2026, 9, 16, 14, 0, 0, DateTimeKind.Utc), 2);

        Assert.Equal(new DateOnly(2026, 8, 31), weeks[0].WeekStart);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~CollectorInsertSqlTests|FullyQualifiedName~IsoWeekTests"`

Expected: FAIL to compile — `GetJavaPackageData` does not exist. (The two `IsoWeekTests` cases will pass once the file compiles; they assert existing behaviour the activity relies on.)

- [ ] **Step 3: Write the activity**

Create `CollectDaprStats/GetJavaPackageData.cs`:

```csharp
using System.Globalization;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly per-version JVM package downloads from Scarf.
    /// </summary>
    /// <remarks>
    /// Unlike the NuGet, npm and PyPI collectors this makes ONE request for the
    /// whole ecosystem, so it needs no per-package fan-out, no rate-limit
    /// spacing and no Check/retry loop. `query=io.dapr*` returned exactly the
    /// eighteen JVM artifacts on 2026-09-15, excluding the `Dapr CLI` package
    /// that `package_id=all` pulls in.
    /// </remarks>
    public class GetJavaPackageData : WorkflowActivity<JavaPackageInput, bool>
    {
        // `Dapr` is capitalised deliberately: /v3/* returns 404
        // {"detail":"Organization not found"} for anything else.
        private const string Owner = "Dapr";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";

        private const string TableName = "java_dapr";

        // One complete ISO week per run. `int`, not `short`, matching
        // npm's record: the column is SMALLINT and the binding widens.
        private const int CollectedOverNumberOfDays = 7;

        // 500 x 7 = 3,500 parameters per statement, far inside Postgres'
        // 65,535 limit. A week is ~800 rows, so two statements.
        private const int RowsPerStatement = 500;

        // Column order below. Only week_start needs a cast.
        private static readonly string?[] ColumnCasts =
            [null, null, null, null, null, "date", null];

        private static readonly string[] KeyColumns =
            ["week_start", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count", "unique_origins",
             "collected_over_number_of_days"];

        private static readonly string OnConflict =
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

        /// <summary>
        /// The chunked upsert. Internal so the composed string can be asserted
        /// without a database.
        /// </summary>
        internal static string BuildInsertSql(int rowCount) =>
            $"insert into {TableName} (package_name, collection_date, package_version, " +
            "download_count, collected_over_number_of_days, week_start, unique_origins) " +
            $"values {SqlValuesBuilder.Build(rowCount, ColumnCasts)} " +
            OnConflict;

        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetJavaPackageData(ScarfExportClient scarf, PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            JavaPackageInput input)
        {
            // [0] is the older of the two, i.e. the week before last. The most
            // recently completed week is deliberately skipped: on 2026-09-15
            // Scarf's newest ingested event was Saturday the 12th, so that
            // week was ~25% short and would never be revisited.
            var week = IsoWeek.CompleteWeeksBefore(input.CollectionDate, 2)[0];

            IReadOnlyList<ScarfAggregationResponse.PackageRow> rows;
            try
            {
                rows = await _scarf.FetchPackageVersionsAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    [],
                    DateOnly.FromDateTime(week.From),
                    DateOnly.FromDateTime(week.To),
                    Breakdown: null,
                    BreakdownSet: "by-version",
                    GroupByArtifact: null,
                    Query: string.Join(',', input.PackageNames)));
            }
            catch (Exception ex)
            {
                // Not rethrown: this runs under Task.WhenAll alongside the
                // other package collectors, and an unhandled exception would
                // fail the whole workflow and skip everything after it.
                Console.WriteLine($"Failed to fetch Scarf Java data: {ex.Message}");
                return false;
            }

            Console.WriteLine(
                $"Java packages week {week.WeekStart:yyyy-MM-dd}: {rows.Count} rows, " +
                $"{rows.Select(r => r.PackageName).Distinct().Count()} packages");

            if (rows.Count == 0)
            {
                // Nothing is deleted before writing, so an empty response is
                // harmless to stored data — but it always means something is
                // wrong upstream.
                Console.WriteLine(
                    "WARNING: Scarf returned zero Java package rows. Likely " +
                    "causes: a revoked token, or a query pattern that no " +
                    "longer matches any registered package.");
                return false;
            }

            if (input.SkipStorage)
            {
                return true;
            }

            foreach (var chunk in rows.Chunk(RowsPerStatement))
            {
                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(row.PackageName);
                    parameters.Add(input.CollectionDate);
                    parameters.Add(row.Version);
                    parameters.Add(row.Downloads);
                    parameters.Add(CollectedOverNumberOfDays);
                    // Per row rather than from the requested window: if Scarf
                    // ever returns a bucket we did not ask for, label it
                    // honestly instead of stamping it with the wrong week.
                    parameters.Add(row.WeekStart.ToString(
                        "yyyy-MM-dd", CultureInfo.InvariantCulture));
                    parameters.Add(row.UniqueOrigins);
                }

                await _output.InsertAsync(
                    BuildInsertSql(chunk.Length), parameters.ToArray());
            }

            return true;
        }
    }

    public record JavaPackageInput(
        string[] PackageNames,
        DateTime CollectionDate,
        bool SkipStorage);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, 216 tests.

If the golden string fails, read the diff carefully — `SqlValuesBuilder` emits `($1,$2,...)` with no spaces after commas, while the hand-written `values ($1, $2, $3)` in the other collectors does have them. The expected string above matches `SqlValuesBuilder`.

- [ ] **Step 5: Commit**

```bash
git add CollectDaprStats/GetJavaPackageData.cs CollectDaprStats.Tests/CollectorInsertSqlTests.cs CollectDaprStats.Tests/IsoWeekTests.cs
git commit -m "feat: GetJavaPackageData activity

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Wire the activity into the workflow

**Files:**
- Modify: `CollectDaprStats/CollectorWorkflow.cs`
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: `JavaPackageInput` and `GetJavaPackageData` from Task 4.
- Produces: `CollectorWorkflowInput.JavaPackageNames` — a `string[]` of package-name DSL patterns, read by Task 6's CI payload.

- [ ] **Step 1: Add the input field**

In `CollectDaprStats/CollectorWorkflow.cs`, add `JavaPackageNames` to the record immediately after `PythonPackageNames`:

```csharp
    public record CollectorWorkflowInput(
        DateTime CollectionDate,
        string[] NuGetPackageNames,
        string[] NpmPackageNames,
        string[] PythonPackageNames,
        string[] JavaPackageNames,
        string[] DockerHubImages,
        bool CollectDiscordData,
        bool CollectGitHubData,
        bool CollectDiagridDashboardData,
        string[] DataDogRumServices,
        string[] DataDogRumIdentifiedServices,
        string[] ScarfBuildingBlockPixelIds,
        string[] ScarfLeaderboardPixelIds,
        string[] ScarfDiagridPagePixelIds,
        bool SkipStorage);
```

- [ ] **Step 2: Add the guarded call site**

In `RunAsync`, immediately after the `PythonPackageNames` block and before `if (packageLoops.Count > 0)`:

```csharp
            // One Scarf request covers all eighteen JVM packages, so this is a
            // plain activity call rather than a Get -> Check -> Sleep loop. It
            // joins packageLoops only to run concurrently with them.
            // `?.` because payloads written before this field existed omit it.
            if (input.JavaPackageNames?.Length > 0)
            {
                packageLoops.Add(context.CallActivityAsync(
                    nameof(GetJavaPackageData),
                    new JavaPackageInput(
                        input.JavaPackageNames,
                        input.CollectionDate,
                        input.SkipStorage)));
            }
```

- [ ] **Step 3: Build and run the suite**

Run: `dotnet build dapr-stats.sln && dotnet test dapr-stats.sln`

Expected: build succeeds, 216 tests pass. A compile error here means a `CollectorWorkflowInput` construction site was missed — there should be none in the main project, since the record is only built from deserialised JSON.

- [ ] **Step 4: Add the field to `local-tests.http`**

Add `"JavaPackageNames" : ["io.dapr*"],` immediately after the `PythonPackageNames` line in the **full** workflow payload, and `"JavaPackageNames" : [],` in the **partial** payload. Then add a third request below the partial one:

```
###
### Start a Java-only CollectorWorkflow
###
// @name wfrequest
POST {{dapr_url}}/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
Content-Type: application/json

{
    "CollectionDate" : "{{currentDate}}",
    "NuGetPackageNames" : [],
    "NpmPackageNames" : [],
    "PythonPackageNames" : [],
    "JavaPackageNames" : ["io.dapr*"],
    "CollectDiscordData" : false,
    "CollectGitHubData" : false,
    "CollectDiagridDashboardData" : false,
    "DockerHubImages" : [],
    "DataDogRumServices" : [],
    "DataDogRumIdentifiedServices" : [],
    "ScarfBuildingBlockPixelIds" : [],
    "ScarfLeaderboardPixelIds" : [],
    "ScarfDiagridPagePixelIds" : [],
    "SkipStorage": true
}
```

Check any other `CollectorWorkflow/start` payloads further down the file and add the field to each.

- [ ] **Step 5: Verify against a live run**

With `SCARF_DAPR_API_TOKEN` set in the environment, run `dapr run -f .` and send the Java-only request above. It has `SkipStorage: true`, so nothing is written.

Expected in the `dapr run` output: a line of the form
`Java packages week 2026-08-31: 7xx rows, 18 packages`.

If it reports 19 packages, the query matched something extra — check for a non-`io.dapr` artifact name. If it reports zero rows, re-read the warning and check the token.

- [ ] **Step 6: Commit**

```bash
git add CollectDaprStats/CollectorWorkflow.cs local-tests.http
git commit -m "feat: collect Java packages from CollectorWorkflow

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Merge the CI package inputs

**Files:**
- Modify: `.github/workflows/run-workflow.yaml`

**Interfaces:**
- Consumes: `CollectorWorkflowInput.JavaPackageNames` from Task 5.
- Produces: nothing consumed by later tasks.

**Why:** `workflow_dispatch` allows at most 10 inputs and the file is at the cap. Merging `nuget_packages`, `npm_packages` and `python_packages` into one `packages` input takes it to 8, leaving room for Java plus a spare.

**Breaking change:** anyone with a bookmarked dispatch form or a saved `gh workflow run` command using `-f nuget_packages=...` must switch to `-f packages=...`. Call this out in the commit message.

- [ ] **Step 1: Replace the three inputs with one**

In the `inputs:` block, delete the `nuget_packages`, `npm_packages` and `python_packages` entries and put this in their place, as the first input:

```yaml
      packages:
        description: 'Packages: all | none | <eco>:<list> groups separated by ; (eco = nuget, npm, python, java). Naming any group skips every ecosystem you do not name. Example: nuget:Dapr.Client,Dapr.Jobs;java:all'
        required: false
        type: string
        default: 'all'
```

Leave the explanatory `# NOTE: workflow_dispatch allows at most 10 inputs.` comment above the block, and update its second paragraph to describe the group syntax rather than three separate lists.

- [ ] **Step 2: Add the Java default**

In the `env:` block, beside the other three:

```yaml
  DEFAULT_JAVA_PACKAGES: 'io.dapr*'
```

Keep the existing comment about this being the one place the lists are defined.

- [ ] **Step 3: Replace the input plumbing**

In the `Generate Variables and Start CollectorWorkflow` step's `env:` block, delete the three `IN_*_PACKAGES` lines and add:

```yaml
          IN_PACKAGES: ${{ inputs.packages }}
```

Then replace the `resolve_list` function and its three call sites with:

```bash
          # The `packages` input: `all` | `none` | `<eco>:<list>` groups joined
          # by `;`. A text input cannot use the boolean trick -- clearing a
          # field makes GitHub substitute the default, so an emptied field is
          # indistinguishable from an omitted one. `none` is the only value
          # GitHub cannot override, so that is how a list is skipped.
          #
          # Naming ANY group skips every ecosystem not named. A group string is
          # written to re-run something specific; quietly collecting four other
          # ecosystems alongside it is the failure that made `none` a keyword.
          #   $1 = ecosystem key (lowercase), $2 = that ecosystem's default list
          resolve_group() {
            local key="$1" default="$2"
            local spec keyword group gkey glist
            spec="$(printf '%s' "${IN_PACKAGES:-}" | tr -d '[:space:]')"
            keyword="$(printf '%s' "$spec" | tr '[:upper:]' '[:lower:]')"

            case "$keyword" in
              ''|all) printf '%s' "$default"; return ;;
              none)   printf '%s' ''; return ;;
            esac

            local IFS=';'
            for group in $spec; do
              gkey="$(printf '%s' "${group%%:*}" | tr '[:upper:]' '[:lower:]')"
              [ "$gkey" = "$key" ] || continue

              glist="${group#*:}"
              case "$(printf '%s' "$glist" | tr '[:upper:]' '[:lower:]')" in
                all)  printf '%s' "$default" ;;
                none) printf '%s' '' ;;
                # Package names keep their original case: NuGet ids are
                # case-sensitive.
                *)    printf '%s' "$glist" ;;
              esac
              return
            done

            printf '%s' ''
          }

          NUGET_CSV=$(resolve_group nuget "$DEFAULT_NUGET_PACKAGES")
          NPM_CSV=$(resolve_group npm "$DEFAULT_NPM_PACKAGES")
          PYTHON_CSV=$(resolve_group python "$DEFAULT_PYTHON_PACKAGES")
          JAVA_CSV=$(resolve_group java "$DEFAULT_JAVA_PACKAGES")
```

- [ ] **Step 4: Add Java to the payload**

In the `jq -nc` invocation, add the argument and the field:

```bash
            --argjson java "$(csv_to_json "$JAVA_CSV")" \
```

```
              PythonPackageNames: $python,
              JavaPackageNames: $java,
```

- [ ] **Step 5: Test `resolve_group` in isolation**

The function is pure shell, so verify it directly before trusting a dispatch. Run this from Git Bash:

```bash
DEFAULT_NUGET_PACKAGES='Dapr.Client,Dapr.Jobs'
DEFAULT_NPM_PACKAGES='@dapr/dapr'
DEFAULT_PYTHON_PACKAGES='dapr,diagrid'
DEFAULT_JAVA_PACKAGES='io.dapr*'

# paste the resolve_group definition from Step 3 here

check() {  # $1 = packages input, $2 = eco, $3 = expected
  IN_PACKAGES="$1"
  got="$(resolve_group "$2" "$(eval echo \$DEFAULT_$(echo "$2" | tr a-z A-Z)_PACKAGES)")"
  if [ "$got" = "$3" ]; then echo "ok    [$1] $2 -> '$got'";
  else echo "FAIL  [$1] $2 -> '$got' (expected '$3')"; fi
}

check ''        nuget  'Dapr.Client,Dapr.Jobs'
check 'all'     java   'io.dapr*'
check 'ALL'     java   'io.dapr*'
check 'none'    nuget  ''
check 'none'    java   ''
check 'java:all'          java   'io.dapr*'
check 'java:all'          nuget  ''
check 'java:all'          npm    ''
check 'nuget:Dapr.Client;java:all' nuget 'Dapr.Client'
check 'nuget:Dapr.Client;java:all' java  'io.dapr*'
check 'nuget:Dapr.Client;java:all' python ''
check 'nuget:none;java:all'        nuget ''
check 'java:io.dapr/dapr-sdk'      java  'io.dapr/dapr-sdk'
check 'nuget:Dapr.Client, Dapr.Jobs' nuget 'Dapr.Client,Dapr.Jobs'
```

Expected: every line prints `ok`. The last case proves whitespace inside a list is stripped; the `nuget -> ''` cases prove naming one group skips the others.

- [ ] **Step 6: Validate the YAML parses**

Run: `python -c "import yaml,sys; d=yaml.safe_load(open('.github/workflows/run-workflow.yaml')); print(len(d['on']['workflow_dispatch']['inputs']), 'inputs')"`

Expected: `8 inputs`. If Python is unavailable, count the entries under `inputs:` by hand — the number must be 8, not 9 or 10.

- [ ] **Step 7: Commit**

```bash
git add .github/workflows/run-workflow.yaml
git commit -m "feat: merge the three package inputs into one, add Java

BREAKING: the nuget_packages, npm_packages and python_packages dispatch
inputs are replaced by a single `packages` input taking `all`, `none`, or
`<eco>:<list>` groups joined by `;`. A saved `gh workflow run -f
nuget_packages=...` must become `-f packages=nuget:...`.

workflow_dispatch allows at most 10 inputs and the file was at the cap;
this takes it to 8, leaving Java a slot and one spare.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `postgres/java-import.md`

**Interfaces:**
- Consumes: everything above. Produces nothing.

- [ ] **Step 1: Add the data source to the README**

In the data-source list, immediately after the Python line (`README.md:10`), add:

```markdown
- Java package downloads for all 18 `io.dapr` and `io.dapr.spring` JVM packages, per version, via Scarf. Scarf ingests two to three days late, so each run collects the week *before* last rather than the week that just ended.
```

Then, in the dispatch-input table, replace the three rows at `README.md:70-72` with one:

```markdown
| `packages` | `all` / `none` / `<eco>:<list>` groups | `all` | Package downloads. `all` collects NuGet, npm, PyPI and Java at their standard lists; `none` skips them all. Groups joined by `;` collect **only** the ecosystems named — `nuget:Dapr.Client;java:all` collects one NuGet package and all Java, and skips npm and PyPI entirely. Ecosystem keys: `nuget`, `npm`, `python`, `java`. |
```

- [ ] **Step 2: Add the gotchas to AGENTS.md**

In the `## Gotchas` section, add these three, in the terse style of the existing entries:

```markdown
- **Scarf ingests two to three days late.** Probed 2026-09-15, its newest
  event anywhere was Saturday the 12th. Every complete week's
  `max(last_seen)` lands on its Sunday; the most recent week's did not. The
  raw effect is a ~25% shortfall that looks exactly like a real decline —
  `dapr-sdk` read 20,413 for a week whose true value was nearer 28,000. This
  is why `GetJavaPackageData` collects the week *before* last,
  `CompleteWeeksBefore(date, 2)[0]`, and why `java_dapr.week_start` sits two
  weeks behind `collection_date` rather than one.
- **`package_id=all` is not JVM-only on the Scarf export.** It pulls in the
  `Dapr CLI` artifact. `query=io.dapr*` returns exactly the eighteen JVM
  packages and is what the collector sends; it also picks up a newly
  registered package with no code change.
- **`ScarfAggregationResponse` has two entry points and they are not
  interchangeable.** `Parse` drops rows with a null `company_name`, which is
  every row of a `by-version` breakdown; `ParsePackageVersions` is the one to
  use for packages. A test asserts `Parse` still drops them, so collapsing the
  two fails the build.
```

Then replace `AGENTS.md:52` with:

```markdown
5. Update all four of: `local-tests.http`, the `FIXED_*`/`DEFAULT_*` env values and inputs in `run-workflow.yaml`, the README data-source list, and `postgres/postgres_schema.psql`. A new *package* ecosystem needs no new dispatch input — add a `DEFAULT_<ECO>_PACKAGES` env value and one `resolve_group` call, since the single `packages` input already carries every ecosystem.
```

- [ ] **Step 3: Mark the manual import superseded**

At the top of `postgres/java-import.md`, add:

```markdown
> **Superseded from September 2026.** `GetJavaPackageData` collects all
> eighteen `io.dapr` JVM packages from Scarf every week into `java_dapr`.
> `java_dapr_sdk` is a frozen archive of this manual import: one package,
> `dapr-sdk`, monthly, 2023-12 to 2025-04. Nothing writes to it any more.
> The procedure below is kept only to explain how those rows were produced.
```

- [ ] **Step 4: Verify the docs match the code**

Re-read each claim you wrote against the file it describes. Specifically confirm: the table name is `java_dapr`, the activity is `GetJavaPackageData`, the dispatch input is `packages`, and the week offset in the prose is two weeks, matching `CompleteWeeksBefore(input.CollectionDate, 2)[0]`.

- [ ] **Step 5: Commit**

```bash
git add README.md AGENTS.md postgres/java-import.md
git commit -m "docs: record Java collection, Scarf's ingestion lag and the package_id trap

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## After the plan

The migration in Task 3 is **not applied** unless the maintainer approved it at Step 3. Until then `GetJavaPackageData` will throw on its first `InsertAsync`, so do not dispatch a non-`SkipStorage` run.

First real run: dispatch with `packages: java:all` and `skip_storage: true` to confirm the shape end to end, then without `skip_storage` to write the first week.
