# Scarf Pixel Event Weekly Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collect, weekly, which companies view which Dapr building-block documentation pages, and which companies visit dapr.io and docs.dapr.io at all, into Postgres via the existing `CollectorWorkflow`.

**Architecture:** Two new Dapr workflow activities each make one HTTP GET to Scarf's `/v3/insights/Dapr/aggregations/export`, covering the last three complete ISO weeks in a single window. One activity groups by referer and company and maps referers onto building blocks; the other groups by company across both pixels. Each writes its three weeks to Postgres with delete-then-insert. Two Postgres views turn the long tables into the pivot table and the leaderboard.

**Tech Stack:** .NET 10, Dapr Workflow (`Dapr.Workflow`), `IHttpClientFactory`, `System.Text.Json`, xUnit 2.9.2, PostgreSQL via the Dapr `daprstats` output binding.

**Spec:** `docs/superpowers/specs/2026-09-01-scarf-event-collection-design.md`

## Global Constraints

- **Do not run any state-changing git command** (`git add`, `git commit`, `git push`, `git reset`, …). The user's global instructions forbid it unless they explicitly ask in the current conversation. Where a task ends with a commit step, **propose** the command and wait for the user to approve it. Read-only git (`status`, `diff`, `log`) is fine.
- Target framework `net10.0`, `Nullable` and `ImplicitUsings` enabled in both projects.
- Source namespace is `DaprStats` with **brace-scoped** namespaces, matching every existing file in `CollectDaprStats/`. Test namespace is `CollectDaprStats.Tests` with a **file-scoped** namespace, matching every existing file in `CollectDaprStats.Tests/`.
- Scarf owner slug is exactly `Dapr`, capitalised. `/v2/*` accepts `dapr`; `/v3/*` returns `404 {"detail":"Organization not found"}` for anything else.
- Scarf base URL: `https://api.scarf.sh`. Auth header: `Authorization: Bearer <token>`.
- Secret store component name is `secretstore`; the Scarf secret key is `SCARF_DAPR_API_TOKEN`.
- Docs pixel id: `4848fb3b-3edb-4329-90a9-a9d79afff054`. dapr.io pixel id: `0f63416a-15c2-4ccd-bae0-001898f75f8f`.
- `WeeksPerRun` is `3` in both activities.
- Table names: `scarf_building_block_views`, `scarf_company_views`. View names: `scarf_building_block_company_view`, `scarf_top_companies_view`.
- **Schema style deviation from the spec.** The spec sketched composite `PRIMARY KEY`s with lowercase types. This plan instead uses `id SERIAL PRIMARY KEY` plus a named `UNIQUE` constraint and uppercase type names, because that is what every one of the eleven existing tables in `postgres/postgres_schema.psql` does — including `datadog_rum`, added most recently. The integrity guarantee is identical; only the style differs.
- Build and test with `dotnet build dapr-stats.sln` and `dotnet test dapr-stats.sln`. Run them from the repository root.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `CollectDaprStats/ScarfReferer.cs` | Pure: map a Scarf `referer` URL to a building-block key, or reject it |
| `CollectDaprStats/ScarfAggregationResponse.cs` | Pure: parse the `{"data":[…]}` export envelope into typed rows |
| `CollectDaprStats/SqlValuesBuilder.cs` | Pure: build the `($1::date,$2),($3::date,$4)` clause of a multi-row INSERT |
| `CollectDaprStats/GetScarfBuildingBlockViews.cs` | Activity: fetch, normalise, re-aggregate and store building-block views. Also declares `ScarfInput` |
| `CollectDaprStats/GetScarfCompanyViews.cs` | Activity: fetch and store the combined company leaderboard |
| `CollectDaprStats.Tests/ScarfRefererTests.cs` | Tests for `ScarfReferer` |
| `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs` | Tests for `ScarfAggregationResponse` |
| `CollectDaprStats.Tests/SqlValuesBuilderTests.cs` | Tests for `SqlValuesBuilder` |

**Modified:**

| File | Change |
|---|---|
| `postgres/postgres_schema.psql` | Append two tables and two views |
| `CollectDaprStats/CollectorWorkflow.cs` | Two fields on `CollectorWorkflowInput`; a Scarf block in `RunAsync` |
| `local-tests.http` | Scarf pixel arrays in the two full payloads; two Scarf-only entries |
| `.github/workflows/run-workflow.yaml` | `SCARF_DAPR_API_TOKEN` env var; pixel arrays in the start payload |
| `CollectDaprStats/secrets.json.example` | `"ScarfDaprApiToken": ""` |
| `README.md` | Scarf in the data-source list; `SCARF_DAPR_API_TOKEN` in the env var list |

**Unchanged:** `CollectDaprStats/Program.cs` — both activities take `IHttpClientFactory`, `PostgresOutput` and `DaprClient`, all already registered, and `AddDaprWorkflow()` discovers activities by convention. `CollectDaprStats/PostgresOuput.cs` — `InsertAsync` already takes arbitrary SQL and a parameter array.

Tasks 1–3 are independent of each other. Task 4 is independent of 1–3. Task 5 depends on 1, 2, 3 and 4. Task 6 depends on 2, 3, 4 and 5. Task 7 depends on 5 and 6. Task 8 is independent but should land last so the CI payload matches the shipped input record.

---

### Task 1: `ScarfReferer` — map a referer URL to a building block

**Files:**
- Create: `CollectDaprStats/ScarfReferer.cs`
- Test: `CollectDaprStats.Tests/ScarfRefererTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class DaprStats.ScarfReferer` with `public const string IndexPage = "(index)"` and `public static bool TryGetBuildingBlock(string? referer, out string buildingBlock)`. Returns `true` and sets `buildingBlock` to a lowercase URL segment (or `IndexPage`) when the referer is a Dapr docs building-block page; returns `false` and sets `buildingBlock` to `string.Empty` otherwise.

**Why this is the fiddliest part:** in one sample week the `referer` field arrived in four messy shapes — versioned subdomains (`v1-17.docs.dapr.io`), third-party mirrors (`dapr.website.cncfstack.com`), query strings (`?utm_source=chatgpt.com`, 285 rows), and localised paths (`/zh-hans/…`). All four must collapse correctly or the table fills with near-duplicate keys.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/ScarfRefererTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfRefererTests
{
    [Theory]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/pubsub-overview/", "pubsub")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/workflow/", "workflow")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/howto-publish-subscribe/", "pubsub")]
    public void TryGetBuildingBlock_CanonicalHost_ReturnsFirstSegmentAfterBuildingBlocks(
        string referer, string expected)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal(expected, block);
    }

    [Theory]
    [InlineData("https://v1-17.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    [InlineData("https://v1-13-1.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    [InlineData("https://v1-9.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    public void TryGetBuildingBlock_VersionedDocsHost_IsAccepted(string referer)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal("actors", block);
    }

    [Theory]
    [InlineData("https://dapr.website.cncfstack.com/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://blue-dune-0da9d541e.7.azurestaticapps.net/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://docs.dapr.iox/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://dapr.io/developing-applications/building-blocks/pubsub/")]
    public void TryGetBuildingBlock_ForeignHost_IsRejected(string referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out _));
    }

    [Fact]
    public void TryGetBuildingBlock_QueryStringAndFragment_AreIgnored()
    {
        // 285 rows in the sample week carried ?utm_source= or ?ref=. They must
        // collapse onto the same key as the bare URL.
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/jobs/jobs-overview/?utm_source=chatgpt.com#scheduling",
            out var block));

        Assert.Equal("jobs", block);
    }

    [Fact]
    public void TryGetBuildingBlock_LocalisedPath_YieldsTheSameKeyAsEnglish()
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/zh-hans/developing-applications/building-blocks/state-management/state-management-overview/",
            out var localised));
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/state-management/state-management-overview/",
            out var english));

        Assert.Equal("state-management", english);
        Assert.Equal(english, localised);
    }

    [Theory]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks")]
    [InlineData("https://docs.dapr.io/zh-hans/developing-applications/building-blocks/")]
    public void TryGetBuildingBlock_IndexPage_YieldsTheIndexSentinel(string referer)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal("(index)", block);
    }

    [Theory]
    [InlineData("https://docs.dapr.io/concepts/overview/")]
    [InlineData("https://docs.dapr.io/")]
    [InlineData("https://docs.dapr.io/reference/cli/dapr-init/")]
    public void TryGetBuildingBlock_NonBuildingBlockPage_IsRejected(string referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/developing-applications/building-blocks/pubsub/")]
    public void TryGetBuildingBlock_UnusableInput_IsRejected(string? referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal(string.Empty, block);
    }

    [Fact]
    public void TryGetBuildingBlock_MixedCase_IsLowercased()
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://DOCS.DAPR.IO/developing-applications/building-blocks/PubSub/",
            out var block));

        Assert.Equal("pubsub", block);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~ScarfRefererTests`
Expected: compile error — `The name 'ScarfReferer' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/ScarfReferer.cs`:

```csharp
using System.Text.RegularExpressions;

namespace DaprStats
{
    /// <summary>
    /// Maps a Scarf `referer` value onto the Dapr building block whose
    /// documentation page it points at.
    /// </summary>
    /// <remarks>
    /// Both Dapr pixels set `referrerPolicy="no-referrer-when-downgrade"` on
    /// HTTPS pages, so Scarf receives the full page URL. That URL arrives in
    /// several shapes for the same logical page — versioned subdomains,
    /// third-party mirrors, tracking query strings and localised paths — and
    /// all of them have to collapse onto one key.
    /// </remarks>
    public static class ScarfReferer
    {
        /// <summary>
        /// The key used for `/building-blocks/` itself. Parenthesised so it can
        /// never collide with a real URL segment.
        /// </summary>
        public const string IndexPage = "(index)";

        private const string CanonicalHost = "docs.dapr.io";
        private const string BuildingBlocksSegment = "/building-blocks/";
        private const string BuildingBlocksIndex = "/building-blocks";

        // Versioned docs subdomains: v1-9, v1-17, v1-13-1.
        private static readonly Regex VersionedHost = new(
            @"^v\d+(-\d+)+\.docs\.dapr\.io$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryGetBuildingBlock(string? referer, out string buildingBlock)
        {
            buildingBlock = string.Empty;

            if (string.IsNullOrWhiteSpace(referer) ||
                !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!IsDaprDocsHost(uri.Host))
            {
                return false;
            }

            // AbsolutePath excludes the query and the fragment, which is what
            // collapses the ?utm_source= and ?ref= variants onto one key.
            var path = uri.AbsolutePath;

            if (path.EndsWith(BuildingBlocksIndex, StringComparison.OrdinalIgnoreCase))
            {
                buildingBlock = IndexPage;
                return true;
            }

            // No special handling for language prefixes is needed: searching for
            // the segment rather than anchoring at the path root makes
            // /zh-hans/developing-applications/building-blocks/pubsub/ and
            // /developing-applications/building-blocks/pubsub/ yield the same key.
            var start = path.IndexOf(BuildingBlocksSegment, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            var remainder = path[(start + BuildingBlocksSegment.Length)..];
            var slash = remainder.IndexOf('/');
            var segment = slash < 0 ? remainder : remainder[..slash];

            buildingBlock = segment.Length == 0
                ? IndexPage
                : Uri.UnescapeDataString(segment).ToLowerInvariant();

            return true;
        }

        private static bool IsDaprDocsHost(string host) =>
            host.Equals(CanonicalHost, StringComparison.OrdinalIgnoreCase) ||
            VersionedHost.IsMatch(host);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~ScarfRefererTests`
Expected: PASS, 24 tests.

- [ ] **Step 5: Propose the commit**

Per the global constraint, do not run this. Show it and wait for approval:

```bash
git add CollectDaprStats/ScarfReferer.cs CollectDaprStats.Tests/ScarfRefererTests.cs
git commit -m "feat: add ScarfReferer to map referer URLs to building blocks"
```

---

### Task 2: `ScarfAggregationResponse` — parse the export envelope

**Files:**
- Create: `CollectDaprStats/ScarfAggregationResponse.cs`
- Test: `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class DaprStats.ScarfAggregationResponse` with a nested `public sealed record Row(DateOnly WeekStart, string? Referer, string CompanyName, string CompanyDomain, long Total, long UniqueOrigins)` and `public static IReadOnlyList<Row> Parse(ReadOnlySpan<byte> json)`. Throws `FormatException` on an empty body, invalid JSON, a missing `data` array, or an unparseable `date`.

The response rows carry ~30 fields, almost all null for any given breakdown. Only six matter.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/ScarfAggregationResponseTests.cs`:

```csharp
using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfAggregationResponseTests
{
    private static IReadOnlyList<ScarfAggregationResponse.Row> Parse(string json) =>
        ScarfAggregationResponse.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_BuildingBlockRow_ReadsTheSixFieldsThatMatter()
    {
        var rows = Parse("""
        {"data":[{
          "date":"2026-08-24","artifact":"4848fb3b-3edb-4329-90a9-a9d79afff054",
          "artifact_name":"Dapr Docs","artifact_type":"tracking-pixel",
          "rollup":"weekly","breakdown":"by-referer",
          "breakdowns":["by-referer","by-company"],
          "country":null,"cloud_provider_name":null,"platform":null,"version":null,
          "company_name":"Apple","company_domain":"apple.com",
          "referer":"https://docs.dapr.io/developing-applications/building-blocks/workflow/workflow-overview/",
          "endpoint_id":null,"origin_id":null,"user_agent":null,"points":50,
          "company_sic_codes":[],"company_count":1,
          "last_seen":"2026-08-26 11:16:21+00:00",
          "total":7,"unique_origins":3,"unique_endpoints":1
        }]}
        """);

        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 8, 24), row.WeekStart);
        Assert.Equal(
            "https://docs.dapr.io/developing-applications/building-blocks/workflow/workflow-overview/",
            row.Referer);
        Assert.Equal("Apple", row.CompanyName);
        Assert.Equal("apple.com", row.CompanyDomain);
        Assert.Equal(7, row.Total);
        Assert.Equal(3, row.UniqueOrigins);
    }

    [Fact]
    public void Parse_LeaderboardRow_HasNoReferer()
    {
        // breakdown=by-company with group_by_artifact=false returns artifact ""
        // and a null referer.
        var rows = Parse("""
        {"data":[{
          "date":"2026-08-24","artifact":"","artifact_name":"",
          "artifact_type":"tracking-pixel","rollup":"weekly","breakdown":"by-company",
          "breakdowns":["by-company"],"referer":null,
          "company_name":"CRED","company_domain":"cred.club",
          "total":12,"unique_origins":4
        }]}
        """);

        var row = Assert.Single(rows);
        Assert.Null(row.Referer);
        Assert.Equal("CRED", row.CompanyName);
        Assert.Equal(12, row.Total);
    }

    [Fact]
    public void Parse_NullCompanyDomain_BecomesEmptyString()
    {
        // company_domain is nullable in Scarf's own schema and the column it
        // lands in is part of a NOT NULL unique key.
        var rows = Parse("""
        {"data":[{"date":"2026-08-24","referer":null,
          "company_name":"Some Co","company_domain":null,
          "total":1,"unique_origins":1}]}
        """);

        Assert.Equal(string.Empty, Assert.Single(rows).CompanyDomain);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void Parse_RowWithoutACompany_IsDropped(string companyNameJson)
    {
        var rows = Parse($$"""
        {"data":[{"date":"2026-08-24","referer":null,
          "company_name":{{companyNameJson}},"company_domain":"x.com",
          "total":1,"unique_origins":1}]}
        """);

        Assert.Empty(rows);
    }

    [Fact]
    public void Parse_NullCounts_BecomeZero()
    {
        var rows = Parse("""
        {"data":[{"date":"2026-08-24","referer":null,
          "company_name":"Some Co","company_domain":"x.com",
          "total":null,"unique_origins":null}]}
        """);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Total);
        Assert.Equal(0, row.UniqueOrigins);
    }

    [Fact]
    public void Parse_EmptyDataArray_ReturnsNoRows()
    {
        // A week with no attributed traffic is a real zero, not an error.
        Assert.Empty(Parse("""{"data":[]}"""));
    }

    [Fact]
    public void Parse_MultipleWeeks_KeepsThemDistinct()
    {
        var rows = Parse("""
        {"data":[
          {"date":"2026-08-10","referer":null,"company_name":"A","company_domain":"a.com","total":1,"unique_origins":1},
          {"date":"2026-08-17","referer":null,"company_name":"A","company_domain":"a.com","total":2,"unique_origins":1},
          {"date":"2026-08-24","referer":null,"company_name":"A","company_domain":"a.com","total":3,"unique_origins":1}
        ]}
        """);

        Assert.Equal(
            new[] { new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 24) },
            rows.Select(r => r.WeekStart).ToArray());
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(() => ScarfAggregationResponse.Parse([]));
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("not json"));
    }

    [Fact]
    public void Parse_MissingDataArray_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{"detail":"Organization not found"}"""));
    }

    [Fact]
    public void Parse_UnparseableDate_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""
        {"data":[{"date":"24-08-2026","referer":null,
          "company_name":"A","company_domain":"a.com","total":1,"unique_origins":1}]}
        """));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~ScarfAggregationResponseTests`
Expected: compile error — `The name 'ScarfAggregationResponse' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/ScarfAggregationResponse.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parses the `{"data":[...]}` envelope returned by
    /// GET /v3/insights/{owner}/aggregations/export?format=json.
    /// </summary>
    /// <remarks>
    /// Each row carries around thirty fields, almost all null for any given
    /// breakdown. Only the six read here are used.
    /// </remarks>
    public static class ScarfAggregationResponse
    {
        public sealed record Row(
            DateOnly WeekStart,
            string? Referer,
            string CompanyName,
            string CompanyDomain,
            long Total,
            long UniqueOrigins);

        public static IReadOnlyList<Row> Parse(ReadOnlySpan<byte> json)
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

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Scarf response has no data array.");
            }

            var rows = new List<Row>(data.GetArrayLength());

            foreach (var element in data.EnumerateArray())
            {
                var companyName = ReadString(element, "company_name");

                // Defensive: a by-company breakdown only returns attributed
                // traffic, so this has never been observed. A null here would
                // otherwise reach a NOT NULL key column.
                if (string.IsNullOrWhiteSpace(companyName))
                {
                    continue;
                }

                rows.Add(new Row(
                    ReadWeekStart(element),
                    ReadString(element, "referer"),
                    companyName,
                    ReadString(element, "company_domain") ?? string.Empty,
                    ReadCount(element, "total"),
                    ReadCount(element, "unique_origins")));
            }

            return rows;
        }

        private static DateOnly ReadWeekStart(JsonElement element)
        {
            var text = ReadString(element, "date")
                ?? throw new FormatException("Scarf row has no date.");

            // Parsed exactly, never through DateTime.Parse, which would attach
            // the host's local offset and can shift the calendar day.
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                throw new FormatException($"Scarf row has an unparseable date '{text}'.");
            }

            return date;
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static long ReadCount(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                throw new FormatException($"Scarf row has no '{name}'.");
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.Null => 0L,
                _ => throw new FormatException(
                    $"Scarf '{name}' is {value.ValueKind}, expected a number."),
            };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~ScarfAggregationResponseTests`
Expected: PASS, 12 tests.

- [ ] **Step 5: Propose the commit**

```bash
git add CollectDaprStats/ScarfAggregationResponse.cs CollectDaprStats.Tests/ScarfAggregationResponseTests.cs
git commit -m "feat: add ScarfAggregationResponse parser"
```

---

### Task 3: `SqlValuesBuilder` — multi-row INSERT value clauses

**Files:**
- Create: `CollectDaprStats/SqlValuesBuilder.cs`
- Test: `CollectDaprStats.Tests/SqlValuesBuilderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class DaprStats.SqlValuesBuilder` with `public static string Build(int rowCount, IReadOnlyList<string?> columnCasts)`. `columnCasts` has one entry per column; a non-null entry appends `::<cast>` to that column's placeholder. Throws `ArgumentOutOfRangeException` when `rowCount < 1` and `ArgumentException` when `columnCasts` is empty.

Existing single-row inserts write `$1, $2::date, $3` by hand. Writing 830 rows one statement at a time would be 830 binding invocations per week, so the inserts are chunked, and the parameter numbering has to be generated. This is exactly the off-by-one arithmetic that silently writes the wrong column, so it is separated out and tested.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/SqlValuesBuilderTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class SqlValuesBuilderTests
{
    [Fact]
    public void Build_SingleRowNoCasts_NumbersFromOne()
    {
        Assert.Equal("($1,$2,$3)", SqlValuesBuilder.Build(1, [null, null, null]));
    }

    [Fact]
    public void Build_MultipleRows_ContinuesNumberingAcrossRows()
    {
        Assert.Equal("($1,$2),($3,$4),($5,$6)", SqlValuesBuilder.Build(3, [null, null]));
    }

    [Fact]
    public void Build_LeadingDateCast_AppliesToEveryRow()
    {
        Assert.Equal(
            "($1::date,$2,$3),($4::date,$5,$6)",
            SqlValuesBuilder.Build(2, ["date", null, null]));
    }

    [Fact]
    public void Build_SingleCastColumn_StillCommaSeparatesRows()
    {
        Assert.Equal("($1::date),($2::date)", SqlValuesBuilder.Build(2, ["date"]));
    }

    [Fact]
    public void Build_ChunkOfFiveHundredBySeven_EndsAtParameterThreeThousandFiveHundred()
    {
        // The building-block insert's real chunk shape. 3,500 parameters is far
        // inside Postgres' 65,535 limit.
        var clause = SqlValuesBuilder.Build(500, ["date", null, null, null, null, null, null]);

        Assert.StartsWith("($1::date,$2,$3,$4,$5,$6,$7),", clause);
        Assert.EndsWith(",($3494::date,$3495,$3496,$3497,$3498,$3499,$3500)", clause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_NonPositiveRowCount_Throws(int rowCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlValuesBuilder.Build(rowCount, [null]));
    }

    [Fact]
    public void Build_NoColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlValuesBuilder.Build(1, []));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~SqlValuesBuilderTests`
Expected: compile error — `The name 'SqlValuesBuilder' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/SqlValuesBuilder.cs`:

```csharp
using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Builds the `($1::date,$2),($3::date,$4)` clause of a multi-row INSERT.
    /// </summary>
    /// <remarks>
    /// The Dapr Postgres binding takes one statement and a flat parameter array
    /// per invocation, so writing hundreds of rows one statement at a time
    /// costs hundreds of round trips. Chunked inserts avoid that, at the price
    /// of generating the placeholder numbering — which is why this is a
    /// separate, tested unit rather than string concatenation at the call site.
    /// </remarks>
    public static class SqlValuesBuilder
    {
        /// <param name="rowCount">Rows in this statement. Must be at least one.</param>
        /// <param name="columnCasts">
        /// One entry per column, in column order. A non-null entry appends
        /// `::<cast>` to that column's placeholder; null leaves it bare.
        /// </param>
        public static string Build(int rowCount, IReadOnlyList<string?> columnCasts)
        {
            if (rowCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowCount), rowCount, "At least one row is required.");
            }

            if (columnCasts.Count == 0)
            {
                throw new ArgumentException("At least one column is required.", nameof(columnCasts));
            }

            var builder = new StringBuilder();
            var parameter = 1;

            for (var row = 0; row < rowCount; row++)
            {
                if (row > 0)
                {
                    builder.Append(',');
                }

                builder.Append('(');

                for (var column = 0; column < columnCasts.Count; column++)
                {
                    if (column > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append('$').Append(parameter++);

                    if (columnCasts[column] is { } cast)
                    {
                        builder.Append("::").Append(cast);
                    }
                }

                builder.Append(')');
            }

            return builder.ToString();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln --filter FullyQualifiedName~SqlValuesBuilderTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Propose the commit**

```bash
git add CollectDaprStats/SqlValuesBuilder.cs CollectDaprStats.Tests/SqlValuesBuilderTests.cs
git commit -m "feat: add SqlValuesBuilder for chunked multi-row inserts"
```

---

### Task 4: Postgres schema — two tables and two views

**Files:**
- Modify: `postgres/postgres_schema.psql` (append to the end of the file)

**Interfaces:**
- Consumes: nothing.
- Produces: tables `scarf_building_block_views` and `scarf_company_views`; views `scarf_building_block_company_view` and `scarf_top_companies_view`.

There is no migration runner in this repo — `postgres_schema.psql` is the record of the schema and statements are applied by hand against Neon. Table statements are separated by a blank line; view statements by a `---` line, matching the existing file.

- [ ] **Step 1: Append the two tables**

Add to the end of `postgres/postgres_schema.psql`:

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

`company_name` alone is not a safe key — two companies can share a display name — and `company_domain` alone is not safe while Scarf types it nullable. Both together are.

- [ ] **Step 2: Append the pivot view**

Add, after a `---` separator line:

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

A building block absent from this list still contributes to `total`, so a newly-added Dapr building block shows up as a gap between `total` and the sum of the columns until someone adds a line here.

- [ ] **Step 3: Append the leaderboard view**

Add, after another `---` separator line:

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

`ROW_NUMBER` rather than `RANK`, so a tie cannot return more than 100 rows. `company_name` is the tiebreaker, making the ordering deterministic across runs.

- [ ] **Step 4: Validate the DDL without committing it**

Wrap the four statements in an aborted `DO` block so Postgres parses and plans them without leaving anything behind. Run it against the Neon `daprstats` database through the HTTP `/sql` endpoint.

Expected: the statement errors with the deliberate `RAISE EXCEPTION`, and reports no syntax error before it. Any other error is a real defect in the DDL.

```sql
DO $$
BEGIN
    -- the four statements above, verbatim
    RAISE EXCEPTION 'dry run complete';
END $$;
```

- [ ] **Step 5: Apply the four statements for real**

Run the four statements against the Neon `daprstats` database. Then confirm:

```sql
SELECT table_name FROM information_schema.tables
WHERE table_name LIKE 'scarf%' ORDER BY table_name;
```

Expected four rows: `scarf_building_block_company_view`, `scarf_building_block_views`, `scarf_company_views`, `scarf_top_companies_view`.

- [ ] **Step 6: Propose the commit**

```bash
git add postgres/postgres_schema.psql
git commit -m "feat: add Scarf building block and company view tables"
```

---

### Task 5: `GetScarfBuildingBlockViews` activity

**Files:**
- Create: `CollectDaprStats/GetScarfBuildingBlockViews.cs`

**Interfaces:**
- Consumes: `ScarfReferer.TryGetBuildingBlock` (Task 1), `ScarfAggregationResponse.Parse` and `ScarfAggregationResponse.Row` (Task 2), `SqlValuesBuilder.Build` (Task 3), the `scarf_building_block_views` table (Task 4), and the existing `IsoWeek.CompleteWeeksBefore(DateTime, int)` and `PostgresOutput.InsertAsync(string, object[])`.
- Produces: `public class DaprStats.GetScarfBuildingBlockViews : WorkflowActivity<ScarfInput, bool>` and `public record DaprStats.ScarfInput(string[] PixelIds, bool SkipStorage)`, declared at the bottom of this file the way `DataDogRumInput` is declared at the bottom of `GetDataDogRumData.cs`. Task 6 reuses `ScarfInput`.

Modelled directly on `CollectDaprStats/GetDataDogRumData.cs` — same constructor injection, same secret read, same `IsoWeek` call, same per-week try/catch, same `Console.WriteLine` logging. Read that file first.

- [ ] **Step 1: Write the activity**

Create `CollectDaprStats/GetScarfBuildingBlockViews.cs`:

```csharp
using System.Globalization;
using System.Text;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetScarfBuildingBlockViews : WorkflowActivity<ScarfInput, bool>
    {
        // Complete ISO weeks fetched and rewritten per run. Unlike DataDog RUM
        // this is not a retention limit — Scarf keeps years. Scarf's company
        // attribution is retroactive, so a visitor identified days later
        // changes an earlier week, and the overlap lets that correction land.
        private const int WeeksPerRun = 3;

        // Capitalised deliberately. /v2/* accepts `dapr`, but /v3/* returns
        // 404 {"detail":"Organization not found"} for anything but `Dapr`.
        // Confirmed 2026-09-01.
        private const string Owner = "Dapr";

        private const string ExportUrl =
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export";

        private const string SecretStore = "secretstore";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";

        private const string TableName = "scarf_building_block_views";

        // 500 x 7 = 3,500 parameters per statement, far inside Postgres' 65,535
        // limit. A busy week is ~830 rows, so two statements.
        private const int RowsPerStatement = 500;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null, null];

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetScarfBuildingBlockViews(
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
            ScarfInput input)
        {
            var secrets = await _daprClient.GetSecretAsync(SecretStore, ApiTokenSecret);
            var token = secrets[ApiTokenSecret];

            // One request covers all three weeks: rollup=weekly returns one
            // bucket per week. end_date is exclusive, verified 2026-09-01.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            var rows = await FetchAsync(input.PixelIds, from, to, token);
            var byWeek = Aggregate(rows);

            var allSucceeded = true;

            // Every complete week is written, including one Scarf returned no
            // rows for. A genuinely empty week has to clear the previous
            // collection's rows rather than leave them standing.
            foreach (var week in weeks)
            {
                // Not `: []` — a collection expression in a conditional has no
                // natural type for `var` to infer.
                var weekRows = byWeek.TryGetValue(week.WeekStart, out var found)
                    ? found
                    : new List<BuildingBlockRow>();

                Console.WriteLine(
                    $"Scarf building blocks week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} rows, " +
                    $"{weekRows.Select(r => r.BuildingBlock).Distinct().Count()} building blocks, " +
                    $"{weekRows.Select(r => (r.CompanyName, r.CompanyDomain)).Distinct().Count()} companies");

                if (input.SkipStorage)
                {
                    continue;
                }

                try
                {
                    await StoreAsync(week.WeekStart, weekRows);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to store Scarf building block views for week " +
                        $"{week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            string[] pixelIds, DateOnly from, DateOnly to, string token)
        {
            var url = new StringBuilder(ExportUrl);
            url.Append("?start_date=").Append(Iso(from));
            url.Append("&end_date=").Append(Iso(to));

            foreach (var pixelId in pixelIds)
            {
                url.Append("&tracking_pixel_id=").Append(Uri.EscapeDataString(pixelId));
            }

            url.Append("&rollup=weekly");

            // The comma is left unescaped: this is the exact form verified
            // against the live API on 2026-09-01.
            url.Append("&breakdown_set=by-referer,by-company");
            url.Append("&format=json");

            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            request.Headers.Add("Authorization", $"Bearer {token}");

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Scarf returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return ScarfAggregationResponse.Parse(payload);
        }

        /// <summary>
        /// Maps referers onto building blocks and re-sums the result, keyed by
        /// week.
        /// </summary>
        /// <remarks>
        /// Normalisation is many-to-one: query-string variants, versioned
        /// hosts, localised paths and every page beneath a building block all
        /// collapse onto one key. Without re-summing here the insert would
        /// carry duplicate keys and violate the unique constraint.
        /// </remarks>
        private static Dictionary<DateOnly, List<BuildingBlockRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Block, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                if (!ScarfReferer.TryGetBuildingBlock(row.Referer, out var block))
                {
                    continue;
                }

                var key = (row.WeekStart, block, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               running.UniqueVisitors + row.UniqueOrigins);
            }

            var byWeek = new Dictionary<DateOnly, List<BuildingBlockRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new BuildingBlockRow(
                    key.Block, key.Name, key.Domain, value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<BuildingBlockRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert rather than upsert: Scarf re-attributing a
            // visitor can remove a (building block, company) pair from a week,
            // and an upsert would leave the old pair behind as a phantom for
            // ever. The binding has no transaction, so a failure between the
            // delete and the last insert leaves the week short until the next
            // run's three-week overlap repairs it.
            await _output.InsertAsync(
                $"delete from {TableName} where week_start = $1::date",
                [weekText]);

            if (rows.Count == 0)
            {
                return;
            }

            var collectionDate = DateTime.UtcNow;

            foreach (var chunk in rows.Chunk(RowsPerStatement))
            {
                var sqlText =
                    $"insert into {TableName} " +
                    "(week_start, building_block, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.BuildingBlock);
                    parameters.Add(row.CompanyName);
                    parameters.Add(row.CompanyDomain);
                    parameters.Add(row.Views);
                    parameters.Add(row.UniqueVisitors);
                    parameters.Add(collectionDate);
                }

                await _output.InsertAsync(sqlText, parameters.ToArray());
            }
        }

        private static string Iso(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private sealed record BuildingBlockRow(
            string BuildingBlock,
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }

    public record ScarfInput(string[] PixelIds, bool SkipStorage);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build dapr-stats.sln`
Expected: build succeeded, 0 errors.

- [ ] **Step 3: Run the whole test suite**

Run: `dotnet test dapr-stats.sln`
Expected: PASS. No new tests here — the activity's logic lives in the three units already tested, and the rest is I/O.

- [ ] **Step 4: Propose the commit**

```bash
git add CollectDaprStats/GetScarfBuildingBlockViews.cs
git commit -m "feat: add GetScarfBuildingBlockViews activity"
```

---

### Task 6: `GetScarfCompanyViews` activity

**Files:**
- Create: `CollectDaprStats/GetScarfCompanyViews.cs`

**Interfaces:**
- Consumes: `ScarfAggregationResponse.Parse` and `.Row` (Task 2), `SqlValuesBuilder.Build` (Task 3), the `scarf_company_views` table (Task 4), `ScarfInput` (Task 5), and the existing `IsoWeek` and `PostgresOutput`.
- Produces: `public class DaprStats.GetScarfCompanyViews : WorkflowActivity<ScarfInput, bool>`.

Same shape as Task 5 and simpler: no referer handling. Two deliberate differences from Task 5's request — `breakdown=by-company` instead of `breakdown_set`, and `group_by_artifact=false`, which is what makes Scarf compute distinct origins across both pixels server-side instead of returning per-pixel rows that cannot be summed without double-counting.

- [ ] **Step 1: Write the activity**

Create `CollectDaprStats/GetScarfCompanyViews.cs`:

```csharp
using System.Globalization;
using System.Text;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetScarfCompanyViews : WorkflowActivity<ScarfInput, bool>
    {
        // The same three complete ISO weeks GetScarfBuildingBlockViews writes,
        // so both tables always cover the same range.
        private const int WeeksPerRun = 3;

        private const string ExportUrl =
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export";

        private const string SecretStore = "secretstore";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";

        private const string TableName = "scarf_company_views";

        private const int RowsPerStatement = 500;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null];

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetScarfCompanyViews(
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
            ScarfInput input)
        {
            var secrets = await _daprClient.GetSecretAsync(SecretStore, ApiTokenSecret);
            var token = secrets[ApiTokenSecret];

            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            var rows = await FetchAsync(input.PixelIds, from, to, token);
            var byWeek = Aggregate(rows);

            var allSucceeded = true;

            foreach (var week in weeks)
            {
                // Not `: []` — a collection expression in a conditional has no
                // natural type for `var` to infer.
                var weekRows = byWeek.TryGetValue(week.WeekStart, out var found)
                    ? found
                    : new List<CompanyRow>();

                Console.WriteLine(
                    $"Scarf companies week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} companies, " +
                    $"{weekRows.Sum(r => r.Views)} views");

                if (input.SkipStorage)
                {
                    continue;
                }

                try
                {
                    await StoreAsync(week.WeekStart, weekRows);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to store Scarf company views for week " +
                        $"{week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            string[] pixelIds, DateOnly from, DateOnly to, string token)
        {
            var url = new StringBuilder(ExportUrl);
            url.Append("?start_date=").Append(Iso(from));
            url.Append("&end_date=").Append(Iso(to));

            foreach (var pixelId in pixelIds)
            {
                url.Append("&tracking_pixel_id=").Append(Uri.EscapeDataString(pixelId));
            }

            url.Append("&rollup=weekly");
            url.Append("&breakdown=by-company");

            // group_by_artifact=false is what merges the two pixels
            // server-side. Without it Scarf returns one row per pixel and
            // unique_origins cannot be summed without double-counting anyone
            // who visited both dapr.io and docs.dapr.io.
            url.Append("&group_by_artifact=false");
            url.Append("&format=json");

            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            request.Headers.Add("Authorization", $"Bearer {token}");

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Scarf returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return ScarfAggregationResponse.Parse(payload);
        }

        /// <summary>
        /// Groups rows by week, guarding the unique constraint.
        /// </summary>
        /// <remarks>
        /// With group_by_artifact=false Scarf already returns one row per
        /// (week, company), so this is defensive. Views are summed; unique
        /// visitors take the maximum rather than the sum, because summing
        /// distinct-origin counts is never correct and Max is the identity
        /// when there is only one row, as expected.
        /// </remarks>
        private static Dictionary<DateOnly, List<CompanyRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                var key = (row.WeekStart, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               Math.Max(running.UniqueVisitors, row.UniqueOrigins));
            }

            var byWeek = new Dictionary<DateOnly, List<CompanyRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new CompanyRow(
                    key.Name, key.Domain, value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<CompanyRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert, for the same reason as the building-block
            // table: a company Scarf re-attributes would otherwise linger.
            await _output.InsertAsync(
                $"delete from {TableName} where week_start = $1::date",
                [weekText]);

            if (rows.Count == 0)
            {
                return;
            }

            var collectionDate = DateTime.UtcNow;

            foreach (var chunk in rows.Chunk(RowsPerStatement))
            {
                var sqlText =
                    $"insert into {TableName} " +
                    "(week_start, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.CompanyName);
                    parameters.Add(row.CompanyDomain);
                    parameters.Add(row.Views);
                    parameters.Add(row.UniqueVisitors);
                    parameters.Add(collectionDate);
                }

                await _output.InsertAsync(sqlText, parameters.ToArray());
            }
        }

        private static string Iso(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private sealed record CompanyRow(
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build dapr-stats.sln`
Expected: build succeeded, 0 errors.

- [ ] **Step 3: Run the whole test suite**

Run: `dotnet test dapr-stats.sln`
Expected: PASS.

- [ ] **Step 4: Propose the commit**

```bash
git add CollectDaprStats/GetScarfCompanyViews.cs
git commit -m "feat: add GetScarfCompanyViews activity"
```

---

### Task 7: Wire both activities into `CollectorWorkflow`

**Files:**
- Modify: `CollectDaprStats/CollectorWorkflow.cs` (the `RunAsync` body, and `CollectorWorkflowInput` at the bottom of the file)
- Modify: `local-tests.http`

**Interfaces:**
- Consumes: `GetScarfBuildingBlockViews` and `ScarfInput` (Task 5), `GetScarfCompanyViews` (Task 6).
- Produces: `CollectorWorkflowInput` with two new fields, `string[] ScarfBuildingBlockPixelIds` and `string[] ScarfLeaderboardPixelIds`, both before the trailing `bool SkipStorage`.

Two separate arrays, because the two requests differ in scope: the building-block breakdown only makes sense for the docs pixel, while the leaderboard should span every pixel Dapr owns. One shared array would force the code to know which pixel is "the docs one".

- [ ] **Step 1: Add the two fields to the input record**

In `CollectDaprStats/CollectorWorkflow.cs`, replace the `CollectorWorkflowInput` record with:

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

- [ ] **Step 2: Add the Scarf block to `RunAsync`**

In `CollectDaprStats/CollectorWorkflow.cs`, immediately after the closing brace of the `if (input.DataDogRumServices?.Length > 0)` block and before `if (input.CollectGitHubData)`, insert:

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

- [ ] **Step 3: Build**

Run: `dotnet build dapr-stats.sln`
Expected: build succeeded, 0 errors.

- [ ] **Step 4: Add the pixel arrays to the existing `local-tests.http` payloads**

In `local-tests.http`, in the **"Start a complete CollectorWorkflow"** payload, add after the `"DataDogRumServices"` line:

```json
    "ScarfBuildingBlockPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054"],
    "ScarfLeaderboardPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054", "0f63416a-15c2-4ccd-bae0-001898f75f8f"],
```

In the **"Start a partial CollectorWorkflow"** payload, add after its `"DataDogRumServices"` line:

```json
    "ScarfBuildingBlockPixelIds" : [],
    "ScarfLeaderboardPixelIds" : [],
```

- [ ] **Step 5: Add two Scarf-only entries to `local-tests.http`**

Append to `local-tests.http`:

```http
###
### Scarf only, no storage. Logs three complete ISO weeks for both tables and
### writes nothing. Completes in seconds. Run this first.
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
    "ScarfBuildingBlockPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054"],
    "ScarfLeaderboardPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054", "0f63416a-15c2-4ccd-bae0-001898f75f8f"],
    "SkipStorage": true
}

###
### Scarf only, with storage. Run twice: the second run must not change any
### row count, which is what proves delete-then-insert is idempotent.
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
    "ScarfBuildingBlockPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054"],
    "ScarfLeaderboardPixelIds" : ["4848fb3b-3edb-4329-90a9-a9d79afff054", "0f63416a-15c2-4ccd-bae0-001898f75f8f"],
    "SkipStorage": false
}

###
### Row counts per week, for verifying the two runs above.
###
POST {{dapr_url}}/v1.0/bindings/daprstats
Content-Type: application/json

{
    "operation": "query",
    "metadata": {
        "sql": "select week_start, count(*) from scarf_building_block_views group by week_start order by week_start"
    }
}
```

- [ ] **Step 6: Run the dry-run entry**

Set `SCARF_DAPR_API_TOKEN` in the terminal, start the app with `dapr run -f .`, then send the `SkipStorage: true` entry.

Expected: six log lines, three per activity, with Monday dates and non-zero counts. For example:

```
Scarf building blocks week 2026-08-10: 812 rows, 12 building blocks, 601 companies
Scarf companies week 2026-08-10: 1398 companies, 3844 views
```

Nothing written. If the token is missing the activity throws on `GetSecretAsync` — that is the expected failure, not a bug.

- [ ] **Step 7: Run the storage entry, then verify**

Send the `SkipStorage: false` entry, then run these through the binding and check each:

```sql
-- three distinct Mondays
select distinct week_start from scarf_building_block_views order by week_start;

-- a subset of the known 12 plus (index), nothing else
select distinct building_block from scarf_building_block_views order by building_block;

-- exactly 100 rows for a populated week, rank running 1..100
select count(*), min(rank), max(rank) from scarf_top_companies_view
where week_start = (select max(week_start) from scarf_company_views);

-- the pivot: columns must sum to total
select * from scarf_building_block_company_view
where week_start = (select max(week_start) from scarf_building_block_views)
order by total desc limit 10;
```

- [ ] **Step 8: Run the storage entry a second time and confirm idempotence**

Send the same `SkipStorage: false` entry again, then re-run the row-count query from Step 5.

Expected: identical counts per week. Growth means the delete is not firing and delete-then-insert is broken.

- [ ] **Step 9: Propose the commit**

```bash
git add CollectDaprStats/CollectorWorkflow.cs local-tests.http
git commit -m "feat: collect Scarf pixel events in CollectorWorkflow"
```

---

### Task 8: Secret, CI and documentation

**Files:**
- Modify: `CollectDaprStats/secrets.json.example`
- Modify: `README.md`
- Modify: `.github/workflows/run-workflow.yaml`

**Interfaces:**
- Consumes: the `CollectorWorkflowInput` shape from Task 7. Land this last so the CI payload matches the shipped record.
- Produces: nothing code-facing.

No schedule change. `run-workflow.yaml` already runs at `0 9 * * 1` — 10:00 CET every Monday — which is what a weekly collector needs.

- [ ] **Step 1: Add the secret to the example file**

In `CollectDaprStats/secrets.json.example`, add a `ScarfDaprApiToken` entry after `DataDogAppKey`:

```json
{
    "PostgreSQLConnection": "",
    "DaprDiscordServerId" : "",
    "DiscordBotToken" : "",
    "DaprStatsGitHubPAT" : "",
    "DataDogApiKey" : "",
    "DataDogAppKey" : "",
    "ScarfDaprApiToken" : ""
}
```

- [ ] **Step 2: Update the README**

In `README.md`, add to the data-source list, after the GitHub entry:

```markdown
- Scarf pixel data for `dapr.io` and `docs.dapr.io`:
  - Weekly views of each Dapr building block documentation page, per company
  - Weekly company visit counts across both sites
```

And add to the environment variable list:

```markdown
- `export SCARF_DAPR_API_TOKEN=<SCARF_API_TOKEN_VALUE>`
```

- [ ] **Step 3: Add the secret to the CI workflow**

In `.github/workflows/run-workflow.yaml`, add to the `env:` block after `DATADOGAPPKEY`:

```yaml
  SCARF_DAPR_API_TOKEN: ${{ secrets.SCARF_DAPR_API_TOKEN }}
```

- [ ] **Step 4: Add the pixel arrays to the CI payload**

In `.github/workflows/run-workflow.yaml`, in the `curl` body inside the **"Generate Variables and Start CollectorWorkflow"** step, add after the `"DataDogRumServices"` line:

```json
              "ScarfBuildingBlockPixelIds": ["4848fb3b-3edb-4329-90a9-a9d79afff054"],
              "ScarfLeaderboardPixelIds": ["4848fb3b-3edb-4329-90a9-a9d79afff054", "0f63416a-15c2-4ccd-bae0-001898f75f8f"],
```

- [ ] **Step 5: Check the workflow file still parses**

Run: `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/run-workflow.yaml')); print('ok')"`
Expected: `ok`.

- [ ] **Step 6: Tell the user to create the repository secret**

This cannot be done from here. Report to the user:

> Create a repository secret named `SCARF_DAPR_API_TOKEN` in the `dapr-stats` GitHub repo settings before the next Monday run. Until it exists both Scarf activities throw on `GetSecretAsync` and the scheduled workflow reports failure.

- [ ] **Step 7: Run the full build and test suite**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`
Expected: build succeeded, all tests pass.

- [ ] **Step 8: Propose the commit**

```bash
git add CollectDaprStats/secrets.json.example README.md .github/workflows/run-workflow.yaml
git commit -m "docs: document Scarf collection secret and CI payload"
```

---

## Spec Coverage

| Spec section | Task |
|---|---|
| `scarf_building_block_views` table | 4 |
| `scarf_company_views` table | 4 |
| `scarf_building_block_company_view` pivot | 4 |
| `scarf_top_companies_view` leaderboard | 4 |
| `ScarfReferer` normalisation, all six steps | 1 |
| `ScarfAggregationResponse` | 2 |
| `SqlValuesBuilder` | 3 |
| `GetScarfBuildingBlockViews`, incl. re-aggregation | 5 |
| `GetScarfCompanyViews` | 6 |
| `Program.cs` unchanged | — (explicitly no change; see File Structure) |
| Write strategy: delete-then-insert, 500-row chunks, per-week try/catch | 5, 6 |
| Workflow changes: two input fields, `Task.WhenAll` block | 7 |
| Secrets and CI changes | 8 |
| No schedule change | 8 (stated, no edit) |
| Error handling table | 5, 6 (throw on non-2xx, per-week catch, silent referer drop) |
| Verification: unit tests | 1, 2, 3 |
| Verification: manual, four steps | 7 steps 6–8 |
| Non-goal: no backfill | No task — rolling window only, by construction |
| Non-goal: no `breakdown=by-referer` total-views request | No task |
