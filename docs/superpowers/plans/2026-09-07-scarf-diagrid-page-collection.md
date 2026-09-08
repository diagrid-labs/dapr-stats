# Scarf Diagrid page view collection — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Git policy for this repository.** The repository owner's global
> instructions forbid any agent from running `git add`, `git commit`, `git push`
> or any other state-changing git command unless he asks for it in that
> conversation. The commit steps below are written out so the work is committed
> in sensible units, but **an executing agent must stop and ask before running
> them.** Read-only git commands are fine.

**Goal:** Collect, every week, which companies view which pages on `diagrid.io`
and `docs.diagrid.io`, and report each company's total weekly page views in a
Postgres view.

**Architecture:** A new `GetScarfPageViews` workflow activity fetches Scarf's
`by-referer,by-company` weekly export for the Diagrid account's two pixels,
normalises each referer to a `(site, page_path)` pair, and writes one row per
`(week, site, page, company)` into a new `scarf_diagrid_page_views` table using
the same delete-then-insert-per-ISO-week strategy as the existing Scarf
collectors. The Scarf HTTP call is first extracted from the two existing Dapr
activities into a shared `ScarfExportClient`, so the owner slug and API token
secret become per-activity values instead of single-account constants.

**Tech Stack:** .NET 10, Dapr Workflow 1.18.5, Dapr Postgres output binding
(`bindings.postgresql`), Neon Postgres 16, xUnit 2.9.2.

**Spec:** [docs/superpowers/specs/2026-09-07-scarf-diagrid-page-collection-design.md](../specs/2026-09-07-scarf-diagrid-page-collection-design.md)

## Global Constraints

- Build: `dotnet build dapr-stats.sln`. Test: `dotnet test dapr-stats.sln`. Run: `dapr run -f .`
- **Workflows and activities are never registered explicitly.** `Program.cs` calls the parameterless `AddDaprWorkflow()`; a new `WorkflowActivity<,>` subclass is discovered with no wiring. Only constructor-injected *dependencies* need registering.
- Every `InsertAsync` must be guarded by `if (!input.SkipStorage)` (or an equivalent `continue`), so dry runs write nothing.
- An empty list in `CollectorWorkflowInput` must mean "skip this source" — the GitHub Actions checkboxes rely on it. Guard with `?.Length > 0` for fields that may be omitted from the payload.
- Workflow classes stay deterministic: no `DateTime.Now`, no `Guid.NewGuid()`, no HTTP in `RunAsync`. All I/O lives in activities. Activities *may* call `DateTime.UtcNow`.
- SQL is hand-written with `$1`-style positional parameters through `PostgresOutput`. No EF Core, no direct Npgsql.
- Scarf's v3 owner slug is **case-sensitive**: `Dapr` and `Diagrid`. Lowercase returns `404 {"detail":"Organization not found"}`.
- Scarf `end_date` is **exclusive**. The comma in `breakdown_set=by-referer,by-company` is sent **unescaped**.
- Diagrid pixel IDs: `diagrid.io` = `0a39aa4e-64f2-4851-8a94-2c3417c264ae`, `docs.diagrid.io` = `030637aa-5521-4cbd-b1b7-3f9f97865a0e`.
- Diagrid secret name: `SCARF_DIAGRID_API_TOKEN`.
- `run-workflow.yaml` is at GitHub's **10-input cap for `workflow_dispatch`**. Do not add an input; Diagrid collection rides the existing `collect_scarf` checkbox.
- This repository is public and Actions logs are world-readable. Company names and page paths are business data and are fine to log; never log a secret or an API token.
- Tests are pure unit tests — no network, no database. The suite runs in ~100 ms and must stay that way.

---

### Task 1: Shared `ScarfExportClient`

Extract the Scarf export HTTP call into one testable place. Nothing consumes it
yet; the two Dapr activities are migrated in Task 2. `BuildUrl` is a pure static
so the URL shape — the only part of the extraction that can silently break the
working Dapr collectors — is unit-testable without a network call.

**Files:**
- Create: `CollectDaprStats/ScarfExportClient.cs`
- Modify: `CollectDaprStats/Program.cs` (add the DI registration)
- Test: `CollectDaprStats.Tests/ScarfExportClientTests.cs`

**Interfaces:**
- Consumes: `ScarfAggregationResponse.Parse(ReadOnlySpan<byte>)` and `ScarfAggregationResponse.Row(DateOnly WeekStart, string? Referer, string CompanyName, string CompanyDomain, long Total, long UniqueOrigins)`, both existing and unchanged.
- Produces:
  - `public sealed record ScarfExportRequest(string Owner, string ApiTokenSecret, string[] PixelIds, DateOnly From, DateOnly To, string? Breakdown, string? BreakdownSet, bool? GroupByArtifact)`
  - `internal static string ScarfExportClient.BuildUrl(ScarfExportRequest request)`
  - `public Task<IReadOnlyList<ScarfAggregationResponse.Row>> ScarfExportClient.FetchAsync(ScarfExportRequest request)`

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/ScarfExportClientTests.cs`:

```csharp
using System.Globalization;
using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfExportClientTests
{
    private static readonly DateOnly From = new(2026, 8, 10);
    private static readonly DateOnly To = new(2026, 8, 31);

    private const string DaprDocsPixel = "4848fb3b-3edb-4329-90a9-a9d79afff054";
    private const string DaprSitePixel = "0f63416a-15c2-4ccd-bae0-001898f75f8f";

    // The two assertions below are the regression net for the whole
    // extraction: they are the exact URLs GetScarfBuildingBlockViews and
    // GetScarfCompanyViews built before Task 2 moved them onto this client.
    [Fact]
    public void BuildUrl_BuildingBlockShape_IsUnchangedByTheExtraction()
    {
        var request = new ScarfExportRequest(
            "Dapr",
            "SCARF_DAPR_API_TOKEN",
            [DaprDocsPixel],
            From,
            To,
            Breakdown: null,
            BreakdownSet: "by-referer,by-company",
            GroupByArtifact: null);

        Assert.Equal(
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export" +
            "?start_date=2026-08-10" +
            "&end_date=2026-08-31" +
            $"&tracking_pixel_id={DaprDocsPixel}" +
            "&rollup=weekly" +
            "&breakdown_set=by-referer,by-company" +
            "&format=json",
            ScarfExportClient.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_CompanyShape_IsUnchangedByTheExtraction()
    {
        var request = new ScarfExportRequest(
            "Dapr",
            "SCARF_DAPR_API_TOKEN",
            [DaprDocsPixel, DaprSitePixel],
            From,
            To,
            Breakdown: "by-company",
            BreakdownSet: null,
            GroupByArtifact: false);

        Assert.Equal(
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export" +
            "?start_date=2026-08-10" +
            "&end_date=2026-08-31" +
            $"&tracking_pixel_id={DaprDocsPixel}" +
            $"&tracking_pixel_id={DaprSitePixel}" +
            "&rollup=weekly" +
            "&breakdown=by-company" +
            "&group_by_artifact=false" +
            "&format=json",
            ScarfExportClient.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_NullGroupByArtifact_OmitsTheParameterEntirely()
    {
        // Omitting the parameter and sending group_by_artifact=true are
        // different requests to Scarf. GetScarfBuildingBlockViews omits it;
        // GetScarfCompanyViews sends false. A non-nullable bool could not
        // express both.
        var url = ScarfExportClient.BuildUrl(new ScarfExportRequest(
            "Diagrid", "SCARF_DIAGRID_API_TOKEN", ["p"], From, To,
            Breakdown: null, BreakdownSet: "by-referer,by-company",
            GroupByArtifact: null));

        Assert.DoesNotContain("group_by_artifact", url);
    }

    [Fact]
    public void BuildUrl_BreakdownSetComma_IsNotEscaped()
    {
        // Verified against the live API on 2026-09-01: this is the exact form
        // Scarf accepts. %2C has not been tested and must not be introduced.
        var url = ScarfExportClient.BuildUrl(new ScarfExportRequest(
            "Dapr", "SCARF_DAPR_API_TOKEN", ["p"], From, To,
            Breakdown: null, BreakdownSet: "by-referer,by-company",
            GroupByArtifact: null));

        Assert.Contains("breakdown_set=by-referer,by-company", url);
        Assert.DoesNotContain("%2C", url);
    }

    [Fact]
    public void BuildUrl_OwnerCapitalisation_IsPreserved()
    {
        // The v3 endpoints return 404 "Organization not found" for a
        // lowercased slug, so nothing here may normalise case.
        var url = ScarfExportClient.BuildUrl(new ScarfExportRequest(
            "Diagrid", "SCARF_DIAGRID_API_TOKEN", ["p"], From, To,
            Breakdown: "by-company", BreakdownSet: null,
            GroupByArtifact: false));

        Assert.Contains("/v3/insights/Diagrid/aggregations/export", url);
    }

    [Fact]
    public void BuildUrl_PixelIds_AreUrlEscaped()
    {
        var url = ScarfExportClient.BuildUrl(new ScarfExportRequest(
            "Dapr", "SCARF_DAPR_API_TOKEN", ["a b&c"], From, To,
            Breakdown: "by-company", BreakdownSet: null,
            GroupByArtifact: false));

        Assert.Contains("tracking_pixel_id=a%20b%26c", url);
    }

    [Fact]
    public void BuildUrl_NonInvariantCulture_StillFormatsGregorianIsoDates()
    {
        // th-TH defaults to the Buddhist calendar, so a culture-sensitive
        // ToString would emit 2569-08-10 and Scarf would reject the window.
        // CurrentCulture is per-thread, so this cannot leak into tests running
        // in parallel on other threads.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            var url = ScarfExportClient.BuildUrl(new ScarfExportRequest(
                "Dapr", "SCARF_DAPR_API_TOKEN", ["p"], From, To,
                Breakdown: "by-company", BreakdownSet: null,
                GroupByArtifact: false));

            Assert.Contains("start_date=2026-08-10", url);
            Assert.Contains("end_date=2026-08-31", url);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~ScarfExportClientTests"`

Expected: compilation failure — `ScarfExportRequest` and `ScarfExportClient` do
not exist yet. A build error is the correct "red" here; do not proceed until
you have seen it.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/ScarfExportClient.cs`:

```csharp
using System.Globalization;
using System.Text;
using Dapr.Client;

namespace DaprStats
{
    /// <summary>
    /// One place to call Scarf's aggregation export, for every account and
    /// every breakdown.
    /// </summary>
    /// <remarks>
    /// The owner slug and the API token secret are per-request rather than
    /// compile-time constants, which is what lets a second Scarf account
    /// (Diagrid) be collected alongside the first (Dapr).
    /// </remarks>
    public sealed record ScarfExportRequest(
        string Owner,            // "Dapr" | "Diagrid" — case-sensitive.
        string ApiTokenSecret,   // "SCARF_DAPR_API_TOKEN" | "SCARF_DIAGRID_API_TOKEN"
        string[] PixelIds,
        DateOnly From,
        DateOnly To,             // Exclusive, verified 2026-09-01.
        string? Breakdown,       // "by-company", or null to omit.
        string? BreakdownSet,    // "by-referer,by-company", or null to omit.
        bool? GroupByArtifact);  // null omits the parameter entirely.

    public sealed class ScarfExportClient
    {
        private const string SecretStore = "secretstore";

        private readonly HttpClient _httpClient;
        private readonly DaprClient _daprClient;

        public ScarfExportClient(
            IHttpClientFactory httpClientFactory,
            DaprClient daprClient)
        {
            _httpClient = httpClientFactory.CreateClient();
            _daprClient = daprClient;
        }

        /// <summary>
        /// Builds the export URL. Pure and internal so the exact query shape
        /// can be asserted in tests without an HTTP call.
        /// </summary>
        internal static string BuildUrl(ScarfExportRequest request)
        {
            var url = new StringBuilder("https://api.scarf.sh/v3/insights/");

            // Never case-normalised: /v3/* returns 404
            // {"detail":"Organization not found"} unless the slug matches
            // exactly. Confirmed 2026-09-01.
            url.Append(request.Owner);
            url.Append("/aggregations/export");

            url.Append("?start_date=").Append(Iso(request.From));
            url.Append("&end_date=").Append(Iso(request.To));

            foreach (var pixelId in request.PixelIds)
            {
                url.Append("&tracking_pixel_id=").Append(Uri.EscapeDataString(pixelId));
            }

            url.Append("&rollup=weekly");

            if (request.Breakdown is { } breakdown)
            {
                url.Append("&breakdown=").Append(breakdown);
            }

            if (request.BreakdownSet is { } breakdownSet)
            {
                // The comma is left unescaped: this is the exact form verified
                // against the live API on 2026-09-01.
                url.Append("&breakdown_set=").Append(breakdownSet);
            }

            // Asymmetric by design and not to be "tidied": by-company sends
            // false so the pixels merge server-side and unique_origins is not
            // double-counted; by-referer omits the parameter altogether.
            if (request.GroupByArtifact is { } groupByArtifact)
            {
                url.Append("&group_by_artifact=")
                   .Append(groupByArtifact ? "true" : "false");
            }

            url.Append("&format=json");

            return url.ToString();
        }

        public async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            ScarfExportRequest request)
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

            return ScarfAggregationResponse.Parse(payload);
        }

        private static string Iso(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 4: Register the client for injection**

In `CollectDaprStats/Program.cs`, add one line next to the other singletons —
after `builder.Services.AddSingleton<PostgresOutput>();`:

```csharp
builder.Services.AddSingleton<ScarfExportClient>();
```

`builder.Services.AddHttpClient()` is already called above it, so
`IHttpClientFactory` resolves, and `daprClient` is already registered as a
singleton.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, including all 139 pre-existing tests.

- [ ] **Step 6: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/ScarfExportClient.cs CollectDaprStats/Program.cs CollectDaprStats.Tests/ScarfExportClientTests.cs
git commit -m "feat: add shared ScarfExportClient with a testable URL builder"
```

---

### Task 2: Move the two Dapr activities onto the shared client

Behaviour-preserving refactor. The three-week window, zero-row guard,
delete-then-insert write path, per-week logging and `bool` return all stay
exactly as they are. Only the fetch changes.

**Files:**
- Modify: `CollectDaprStats/GetScarfBuildingBlockViews.cs`
- Modify: `CollectDaprStats/GetScarfCompanyViews.cs`

**Interfaces:**
- Consumes: `ScarfExportRequest` and `ScarfExportClient.FetchAsync` from Task 1.
- Produces: nothing new. `ScarfInput(string[] PixelIds, bool SkipStorage)` stays where it is, at the bottom of `GetScarfBuildingBlockViews.cs`, and Task 5 reuses it.

- [ ] **Step 1: Capture the pre-refactor baseline**

This is the only real regression check on live behaviour, and it has to be taken
*before* the code changes.

```bash
export SCARF_DAPR_API_TOKEN=<token>
dapr run -f .
```

Then send the "Scarf only, no storage" request from `local-tests.http`
(`SkipStorage: true`). Save the per-week log lines — the ones beginning
`Scarf building blocks week` and `Scarf companies week` — to a scratch file. You
will compare against them in Step 5.

- [ ] **Step 2: Refactor `GetScarfBuildingBlockViews`**

Delete the `ExportUrl`, `SecretStore` and `ApiTokenSecret` consts, the
`_httpClient` and `_daprClient` fields, the private `FetchAsync` method and the
private `Iso` method. Add two consts, and replace the constructor and the fetch
call site:

```csharp
        // `Dapr` is capitalised deliberately. /v2/* accepts `dapr`, but /v3/*
        // returns 404 {"detail":"Organization not found"} for anything but
        // `Dapr`. Confirmed 2026-09-01.
        private const string Owner = "Dapr";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";
```

```csharp
        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetScarfBuildingBlockViews(
            ScarfExportClient scarf,
            PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }
```

Inside `RunAsync`, remove the `GetSecretAsync` call at the top (the client does
it now) and replace the `try`/`catch` fetch block's body with:

```csharp
            IReadOnlyList<ScarfAggregationResponse.Row> rows;
            try
            {
                rows = await _scarf.FetchAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    input.PixelIds,
                    from,
                    to,
                    Breakdown: null,
                    BreakdownSet: "by-referer,by-company",
                    GroupByArtifact: null));
            }
            catch (Exception ex)
            {
                // Not rethrown: this activity runs under Task.WhenAll
                // alongside GetScarfCompanyViews in CollectorWorkflow, and an
                // unhandled exception here would fail the whole workflow and
                // skip the unrelated GitHub collection below it. The bool
                // return exists so a Scarf outage is reported without taking
                // anything else down.
                Console.WriteLine(
                    $"Failed to fetch Scarf building block data: {ex.Message}");
                return false;
            }
```

`StoreAsync` still uses `weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`,
so keep the `using System.Globalization;` at the top. `using System.Text;`,
`using Dapr.Client;` and the `IHttpClientFactory` parameter are now unused —
remove them.

- [ ] **Step 3: Refactor `GetScarfCompanyViews`**

The same edits — including deleting the now-unused `using System.Text;` and
`using Dapr.Client;` and keeping `using System.Globalization;` for `StoreAsync`
— with the by-company request shape:

```csharp
        private const string Owner = "Dapr";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";
```

```csharp
        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetScarfCompanyViews(
            ScarfExportClient scarf,
            PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }
```

```csharp
            IReadOnlyList<ScarfAggregationResponse.Row> rows;
            try
            {
                rows = await _scarf.FetchAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    input.PixelIds,
                    from,
                    to,
                    Breakdown: "by-company",
                    BreakdownSet: null,
                    // false is what merges the two pixels server-side. Without
                    // it Scarf returns one row per pixel and unique_origins
                    // cannot be summed without double-counting anyone who
                    // visited both dapr.io and docs.dapr.io.
                    GroupByArtifact: false));
            }
            catch (Exception ex)
            {
                // Not rethrown: see the comment in GetScarfBuildingBlockViews.
                Console.WriteLine(
                    $"Failed to fetch Scarf company data: {ex.Message}");
                return false;
            }
```

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`

Expected: build succeeds with no errors, all tests PASS. Unused usings do not
raise warnings in this project (no analyzer is configured for IDE0005), so
removing them in Steps 2 and 3 is on you, not the compiler.

- [ ] **Step 5: Re-run the dry run and compare to the baseline**

Restart `dapr run -f .` and send the same "Scarf only, no storage" request.

Expected: the `Scarf building blocks week` and `Scarf companies week` lines
match the Step 1 baseline exactly — same weeks, same row counts, same company
counts. A difference means the URL changed; diff `BuildUrl`'s output against the
URL the old code built before going further.

- [ ] **Step 6: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/GetScarfBuildingBlockViews.cs CollectDaprStats/GetScarfCompanyViews.cs
git commit -m "refactor: move the Dapr Scarf collectors onto ScarfExportClient"
```

---

### Task 3: `ScarfPage` referer normalisation

Maps a Scarf `referer` onto the `(site, page_path)` pair that becomes part of a
row's key. Pure function, no dependencies, fully unit-tested.

**Files:**
- Create: `CollectDaprStats/ScarfPage.cs`
- Test: `CollectDaprStats.Tests/ScarfPageTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static bool ScarfPage.TryGetPage(string? referer, out string site, out string path)`. On `true`, `site` is exactly `"diagrid.io"` or `"docs.diagrid.io"` and `path` starts with `/`, is lowercase, and has no trailing slash unless it is the root `"/"`.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/ScarfPageTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfPageTests
{
    [Theory]
    [InlineData("https://docs.diagrid.io/catalyst/concepts/pubsub/", "docs.diagrid.io", "/catalyst/concepts/pubsub")]
    [InlineData("https://diagrid.io/pricing", "diagrid.io", "/pricing")]
    [InlineData("https://www.diagrid.io/pricing", "diagrid.io", "/pricing")]
    [InlineData("https://DOCS.DIAGRID.IO/catalyst/", "docs.diagrid.io", "/catalyst")]
    public void TryGetPage_AllowedHosts_AreAcceptedAndNormalised(
        string referer, string expectedSite, string expectedPath)
    {
        Assert.True(ScarfPage.TryGetPage(referer, out var site, out var path));
        Assert.Equal(expectedSite, site);
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("https://dapr.io/")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://diagrid.iox/pricing")]
    [InlineData("https://staging.diagrid.io/pricing")]
    [InlineData("https://docs.diagrid.io.evil.com/pricing")]
    [InlineData("http://localhost:1313/pricing")]
    public void TryGetPage_ForeignHosts_AreRejected(string referer)
    {
        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/pricing")]
    [InlineData("not a url")]
    public void TryGetPage_MissingOrRelativeReferer_IsRejected(string? referer)
    {
        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }

    [Fact]
    public void TryGetPage_QueryStringAndFragment_AreIgnored()
    {
        // Tracking parameters must collapse onto the same key as the bare URL,
        // or one page fragments into a dozen rows.
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/pricing?utm_source=chatgpt.com&ref=x#plans",
            out var site, out var path));

        Assert.Equal("diagrid.io", site);
        Assert.Equal("/pricing", path);
    }

    [Fact]
    public void TryGetPage_MixedCasePath_IsLowercased()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/Pricing/Enterprise", out _, out var path));

        Assert.Equal("/pricing/enterprise", path);
    }

    [Fact]
    public void TryGetPage_RepeatedSlashes_AreCollapsed()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://docs.diagrid.io//catalyst///concepts/", out _, out var path));

        Assert.Equal("/catalyst/concepts", path);
    }

    [Theory]
    [InlineData("https://diagrid.io/")]
    [InlineData("https://diagrid.io")]
    [InlineData("https://www.diagrid.io/")]
    public void TryGetPage_RootPath_IsStoredAsASingleSlash(string referer)
    {
        Assert.True(ScarfPage.TryGetPage(referer, out _, out var path));
        Assert.Equal("/", path);
    }

    [Fact]
    public void TryGetPage_PercentEncodedSlash_IsUnescapedIntoTheStoredPath()
    {
        // Unlike ScarfReferer there is no key extraction here for a %2F to
        // smuggle a delimiter past: the whole path is the key. Unescaping is
        // what makes /my%20page and "/my page" one row rather than two.
        Assert.True(ScarfPage.TryGetPage(
            "https://docs.diagrid.io/catalyst%2Fconcepts", out _, out var path));

        Assert.Equal("/catalyst/concepts", path);
    }

    [Fact]
    public void TryGetPage_PercentEncodedSpace_IsUnescaped()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/my%20page", out _, out var path));

        Assert.Equal("/my page", path);
    }

    [Fact]
    public void TryGetPage_ControlCharacters_AreRejected()
    {
        Assert.False(ScarfPage.TryGetPage(
            "https://diagrid.io/pri%00cing", out _, out _));
    }

    [Fact]
    public void TryGetPage_PathAtTheColumnWidth_IsAccepted()
    {
        // 512 characters: "/" plus 511. Matches page_path VARCHAR(512).
        var referer = "https://diagrid.io/" + new string('a', 511);

        Assert.True(ScarfPage.TryGetPage(referer, out _, out var path));
        Assert.Equal(512, path.Length);
    }

    [Fact]
    public void TryGetPage_PathOverTheColumnWidth_IsRejected()
    {
        // Rejected rather than truncated: an over-long value would fail its
        // whole insert chunk with Postgres 22001 *after* the week's DELETE had
        // already run, losing the week.
        var referer = "https://diagrid.io/" + new string('a', 512);

        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~ScarfPageTests"`

Expected: compilation failure — `ScarfPage` does not exist.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/ScarfPage.cs`:

```csharp
using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Maps a Scarf `referer` value onto the Diagrid site and page it points
    /// at.
    /// </summary>
    /// <remarks>
    /// The Diagrid pixels set `referrerPolicy="no-referrer-when-downgrade"` on
    /// HTTPS pages, so Scarf receives the full page URL. The same logical page
    /// arrives with tracking query strings, mixed case and inconsistent
    /// trailing slashes, and all of those have to collapse onto one key or a
    /// single page fragments into a dozen rows.
    /// </remarks>
    public static class ScarfPage
    {
        // Matches the width of the page_path column
        // (VARCHAR(512) in postgres/postgres_schema.psql).
        private const int MaxPathLength = 512;

        private const string WebsiteHost = "diagrid.io";
        private const string WwwWebsiteHost = "www.diagrid.io";
        private const string DocsHost = "docs.diagrid.io";

        public static bool TryGetPage(string? referer, out string site, out string path)
        {
            site = string.Empty;
            path = string.Empty;

            if (string.IsNullOrWhiteSpace(referer) ||
                !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!TryNormaliseHost(uri.Host, out var normalisedSite))
            {
                return false;
            }

            // AbsolutePath excludes the query and the fragment, which is what
            // collapses the ?utm_source= and ?ref= variants onto one key.
            if (!TryNormalisePath(uri.AbsolutePath, out var normalisedPath))
            {
                return false;
            }

            site = normalisedSite;
            path = normalisedPath;
            return true;
        }

        /// <summary>
        /// Exact-match allow-list. Anything else — third-party mirrors,
        /// staging and preview hosts, localhost, and any Dapr traffic that
        /// would appear if a pixel were ever shared between the two Scarf
        /// accounts — is dropped.
        /// </summary>
        private static bool TryNormaliseHost(string host, out string site)
        {
            if (host.Equals(DocsHost, StringComparison.OrdinalIgnoreCase))
            {
                site = DocsHost;
                return true;
            }

            if (host.Equals(WebsiteHost, StringComparison.OrdinalIgnoreCase) ||
                host.Equals(WwwWebsiteHost, StringComparison.OrdinalIgnoreCase))
            {
                site = WebsiteHost;
                return true;
            }

            site = string.Empty;
            return false;
        }

        private static bool TryNormalisePath(string absolutePath, out string path)
        {
            path = string.Empty;

            // Unescaped first, so the checks below run on the text that will
            // actually be stored rather than on its encoded form.
            var unescaped = Uri.UnescapeDataString(absolutePath);

            if (unescaped.Any(char.IsControl))
            {
                return false;
            }

            // Lowercased so /Pricing and /pricing do not become two rows.
            var lowered = unescaped.ToLowerInvariant();

            var builder = new StringBuilder(lowered.Length + 1);
            builder.Append('/');

            foreach (var character in lowered)
            {
                if (character == '/' && builder[^1] == '/')
                {
                    continue;
                }

                builder.Append(character);
            }

            // Trailing slash stripped so /pricing/ and /pricing are one row.
            // The root is the exception; it would otherwise become empty.
            if (builder.Length > 1 && builder[^1] == '/')
            {
                builder.Length--;
            }

            if (builder.Length > MaxPathLength)
            {
                // Rejected rather than truncated: an over-long value would
                // fail its whole insert chunk with Postgres error 22001 after
                // the week's DELETE has already run.
                return false;
            }

            path = builder.ToString();
            return true;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, all tests.

- [ ] **Step 5: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/ScarfPage.cs CollectDaprStats.Tests/ScarfPageTests.cs
git commit -m "feat: add ScarfPage referer-to-page normalisation for the Diagrid sites"
```

---

### Task 4: Database table, index and view

The table has to exist before the activity in Task 5 can be exercised against
the real database.

**Files:**
- Create: `postgres/add-scarf-diagrid-page-views-2026-09-07.psql`
- Modify: `postgres/postgres_schema.psql` (append after the `scarf_top_companies_view` block at the end of the Scarf section)

**Interfaces:**
- Consumes: nothing.
- Produces: table `scarf_diagrid_page_views` with columns `id, week_start, site, page_path, company_name, company_domain, views, unique_visitors, collection_date` in that order — Task 5's `INSERT` column list and `ColumnCasts` array depend on it.

- [ ] **Step 1: Write the migration script**

Create `postgres/add-scarf-diagrid-page-views-2026-09-07.psql`:

```sql
-- Adds weekly Scarf page-view collection for the Diagrid account
-- (diagrid.io and docs.diagrid.io), one row per week per site per page per
-- company.
--
-- Additive only: no existing table or view is touched. The Dapr Scarf tables
-- keep their current shape because Diagrid rows land here instead of being
-- mixed in, which is why no `account` column is added to them.

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

-- The unique constraint's own index leads with week_start but puts site and
-- page_path ahead of the company columns, so it does not serve the view's
-- GROUP BY. This table reaches ~1M rows within a year, so the view needs its
-- own index.
CREATE INDEX scarf_diagrid_page_views_week_company
    ON scarf_diagrid_page_views (week_start, company_name, company_domain);

-- total_views is the headline number: how many pages a company viewed that
-- week across both sites.
--
-- unique_visitors is deliberately absent. It is stored per page and summing
-- distinct counts across pages is never correct — one person reading five
-- pages would count five times.
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

- [ ] **Step 2: Dry-run the DDL against Neon**

There is no `psql` on this machine and the Neon CLI has no SQL subcommand, so
statements go over Neon's HTTP `/sql` endpoint with the connection string in a
`Neon-Connection-String` header (see the Database section of `AGENTS.md`).

Validate without committing by wrapping the DDL in a `DO` block that raises at
the end, so the whole thing rolls back:

```sql
DO $$
BEGIN
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
    RAISE EXCEPTION 'DRY RUN OK - rolling back';
END $$;
```

Expected: SQLSTATE `P0001` with the message `DRY RUN OK - rolling back`, which
means every statement compiled and type-checked and nothing persisted. Any
other SQLSTATE is a real DDL problem — fix it before Step 3.

- [ ] **Step 3: Apply the migration for real**

**Ask the repository owner before running this step.** The same caution that
governs git commands here governs his production Neon database: dry-run first,
report, then let him decide whether to apply.

Send the three statements from Step 1 over the `/sql` endpoint, then confirm:

```sql
SELECT column_name, data_type, character_maximum_length
FROM information_schema.columns
WHERE table_name = 'scarf_diagrid_page_views'
ORDER BY ordinal_position;
```

Expected: the nine columns in the order listed above, with `page_path` at
length 512.

```sql
SELECT * FROM scarf_diagrid_company_pages_view;
```

Expected: zero rows, no error.

- [ ] **Step 4: Append the same DDL to the schema file**

Copy the three statements into `postgres/postgres_schema.psql`, after the
`scarf_top_companies_view` definition that currently ends the file, separated by
the file's existing `---` divider convention.

- [ ] **Step 5: Commit** *(ask the repository owner before running any git command)*

```bash
git add postgres/add-scarf-diagrid-page-views-2026-09-07.psql postgres/postgres_schema.psql
git commit -m "feat: add scarf_diagrid_page_views table, index and company view"
```

---

### Task 5: `GetScarfPageViews` activity and workflow wiring

**Files:**
- Create: `CollectDaprStats/GetScarfPageViews.cs`
- Modify: `CollectDaprStats/CollectorWorkflow.cs` (the `scarfTasks` block, and the `CollectorWorkflowInput` record at the bottom)
- Test: `CollectDaprStats.Tests/GetScarfPageViewsTests.cs`

**Interfaces:**
- Consumes: `ScarfExportClient.FetchAsync` / `ScarfExportRequest` (Task 1); `ScarfPage.TryGetPage` (Task 3); `ScarfInput(string[] PixelIds, bool SkipStorage)` (existing, declared at the bottom of `GetScarfBuildingBlockViews.cs`); `IsoWeek.CompleteWeeksBefore(DateTime, int)`; `SqlValuesBuilder.Build(int, IReadOnlyList<string?>)`; `PostgresOutput.InsertAsync(string, object[])`; the table from Task 4.
- Produces: `CollectorWorkflowInput.ScarfDiagridPagePixelIds` (`string[]`), consumed by Task 6's GitHub Actions payload and `local-tests.http` entries. `internal static Dictionary<DateOnly, List<GetScarfPageViews.PageRow>> GetScarfPageViews.Aggregate(IReadOnlyList<ScarfAggregationResponse.Row>)` and `internal sealed record GetScarfPageViews.PageRow(string Site, string PagePath, string CompanyName, string CompanyDomain, long Views, long UniqueVisitors)`, both internal purely so `Aggregate` can be tested.

- [ ] **Step 1: Write the failing tests for `Aggregate`**

`Aggregate` is the one piece of the activity that is pure and worth testing:
it drops referers that do not resolve and re-sums the survivors, and a mistake
there produces a duplicate key that fails the insert *after* the week's DELETE.

Create `CollectDaprStats.Tests/GetScarfPageViewsTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class GetScarfPageViewsTests
{
    private static readonly DateOnly Week = new(2026, 8, 24);

    private static ScarfAggregationResponse.Row Row(
        string? referer, string company, long total, long uniqueOrigins) =>
        new(Week, referer, company, $"{company.ToLowerInvariant()}.com",
            total, uniqueOrigins);

    [Fact]
    public void Aggregate_RefererVariantsOfOnePage_CollapseIntoOneRow()
    {
        // Without the re-sum these three produce the same key three times and
        // the insert violates scarf_diagrid_page_views_unique.
        var rows = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/pricing", "Acme", 3, 2),
            Row("https://diagrid.io/pricing/", "Acme", 4, 1),
            Row("https://www.diagrid.io/Pricing?utm_source=x", "Acme", 5, 3),
        ]);

        var page = Assert.Single(rows[Week]);
        Assert.Equal("diagrid.io", page.Site);
        Assert.Equal("/pricing", page.PagePath);
        Assert.Equal("Acme", page.CompanyName);
        Assert.Equal(12, page.Views);
        Assert.Equal(6, page.UniqueVisitors);
    }

    [Fact]
    public void Aggregate_DifferentSitesWithTheSamePath_StayDistinct()
    {
        var rows = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/catalyst", "Acme", 1, 1),
            Row("https://docs.diagrid.io/catalyst", "Acme", 2, 1),
        ]);

        Assert.Equal(2, rows[Week].Count);
        Assert.Contains(rows[Week], r => r.Site == "diagrid.io");
        Assert.Contains(rows[Week], r => r.Site == "docs.diagrid.io");
    }

    [Fact]
    public void Aggregate_DifferentCompaniesOnOnePage_StayDistinct()
    {
        var rows = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/pricing", "Acme", 1, 1),
            Row("https://diagrid.io/pricing", "Globex", 2, 1),
        ]);

        Assert.Equal(2, rows[Week].Count);
    }

    [Fact]
    public void Aggregate_UnresolvableReferers_AreDropped()
    {
        var rows = GetScarfPageViews.Aggregate([
            Row("https://docs.dapr.io/concepts/", "Acme", 9, 9),
            Row(null, "Acme", 9, 9),
            Row("https://diagrid.io/pricing", "Acme", 1, 1),
        ]);

        var page = Assert.Single(rows[Week]);
        Assert.Equal("/pricing", page.PagePath);
        Assert.Equal(1, page.Views);
    }

    [Fact]
    public void Aggregate_NoResolvableRows_ReturnsAnEmptyDictionary()
    {
        // This is what the activity's zero-row guard keys off, so it must be
        // empty rather than a week mapped to an empty list.
        var rows = GetScarfPageViews.Aggregate([
            Row("https://docs.dapr.io/concepts/", "Acme", 9, 9),
        ]);

        Assert.Empty(rows);
    }

    [Fact]
    public void Aggregate_RowsFromDifferentWeeks_AreKeyedSeparately()
    {
        var earlier = new DateOnly(2026, 8, 17);

        var rows = GetScarfPageViews.Aggregate([
            new ScarfAggregationResponse.Row(
                earlier, "https://diagrid.io/pricing", "Acme", "acme.com", 1, 1),
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", "Acme", "acme.com", 2, 1),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[earlier][0].Views);
        Assert.Equal(2, rows[Week][0].Views);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dapr-stats.sln --filter "FullyQualifiedName~GetScarfPageViewsTests"`

Expected: compilation failure — `GetScarfPageViews` does not exist.

- [ ] **Step 3: Write the activity**

Create `CollectDaprStats/GetScarfPageViews.cs`:

```csharp
using System.Globalization;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly page views on diagrid.io and docs.diagrid.io, per company, from
    /// the Diagrid Scarf account.
    /// </summary>
    public class GetScarfPageViews : WorkflowActivity<ScarfInput, bool>
    {
        // The same three complete ISO weeks the two Dapr Scarf collectors
        // write. Scarf's company attribution is retroactive — a visitor
        // identified days later changes an earlier week — and the overlap is
        // what lets that correction land.
        private const int WeeksPerRun = 3;

        // Capitalised deliberately: /v3/* returns 404
        // {"detail":"Organization not found"} for anything but `Diagrid`.
        private const string Owner = "Diagrid";
        private const string ApiTokenSecret = "SCARF_DIAGRID_API_TOKEN";

        private const string TableName = "scarf_diagrid_page_views";

        // 1000 x 8 = 8,000 parameters per statement, far inside Postgres'
        // 65,535 limit. Higher than the Dapr collectors' 500 because this
        // table carries an order of magnitude more rows per week — hundreds of
        // pages against hundreds of companies rather than thirteen building
        // blocks.
        private const int RowsPerStatement = 1000;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null, null, null];

        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetScarfPageViews(ScarfExportClient scarf, PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            ScarfInput input)
        {
            // One request covers all three weeks: rollup=weekly returns one
            // bucket per week. end_date is exclusive.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            IReadOnlyList<ScarfAggregationResponse.Row> rows;
            try
            {
                rows = await _scarf.FetchAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    input.PixelIds,
                    from,
                    to,
                    Breakdown: null,
                    BreakdownSet: "by-referer,by-company",
                    // Omitted, not false. The two Diagrid pixels serve
                    // different hosts, so the (week, referer, company) keys
                    // they produce are already disjoint and there is nothing
                    // to merge server-side.
                    GroupByArtifact: null));
            }
            catch (Exception ex)
            {
                // Not rethrown: this activity runs under Task.WhenAll
                // alongside the two Dapr Scarf activities in
                // CollectorWorkflow, and an unhandled exception here would
                // fail the whole workflow and skip the unrelated GitHub
                // collection below it. The bool return exists so a Scarf
                // outage is reported without taking anything else down.
                Console.WriteLine(
                    $"Failed to fetch Scarf Diagrid page data: {ex.Message}");
                return false;
            }

            var byWeek = Aggregate(rows);

            // Zero rows across every week, from here, is indistinguishable
            // between a genuinely quiet three weeks and two failure modes that
            // both look healthy: Scarf answering an unrecognised
            // tracking_pixel_id with HTTP 200 and {"data":[]}, or a host
            // rename that stops ScarfPage matching any referer. Either would
            // otherwise delete three real weeks of data below and write
            // nothing back, and the three-week window means the oldest of them
            // is gone for good before the next run could repair it. Bail out
            // before the first DELETE rather than risk that.
            if (byWeek.Values.Sum(list => list.Count) == 0)
            {
                Console.WriteLine(
                    "WARNING: Scarf Diagrid pages returned zero rows across " +
                    $"all {WeeksPerRun} weeks. Skipping storage entirely (no " +
                    "deletes performed). Likely causes: a wrong or revoked " +
                    "tracking_pixel_id, or a host change that moved the sites " +
                    "out of ScarfPage's allow-list.");
                return false;
            }

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
                    : new List<PageRow>();

                Console.WriteLine(
                    $"Scarf Diagrid pages week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} rows, " +
                    $"{weekRows.Select(r => (r.Site, r.PagePath)).Distinct().Count()} pages, " +
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
                        $"Failed to store Scarf Diagrid page views for week " +
                        $"{week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        /// <summary>
        /// Maps referers onto (site, page) pairs and re-sums the result, keyed
        /// by week. Rows whose referer is not a Diagrid page are dropped.
        /// </summary>
        /// <remarks>
        /// Normalisation is many-to-one: query-string variants, `www.`, mixed
        /// case and trailing slashes all collapse onto one key. Without
        /// re-summing here the insert would carry duplicate keys and violate
        /// the unique constraint. Internal so it can be tested.
        /// </remarks>
        internal static Dictionary<DateOnly, List<PageRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Site, string Path, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                if (!ScarfPage.TryGetPage(row.Referer, out var site, out var path))
                {
                    continue;
                }

                var key = (row.WeekStart, site, path, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               running.UniqueVisitors + row.UniqueOrigins);
            }

            var byWeek = new Dictionary<DateOnly, List<PageRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new PageRow(
                    key.Site, key.Path, key.Name, key.Domain,
                    value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<PageRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert rather than upsert: Scarf re-attributing a
            // visitor can remove a (page, company) pair from a week, and an
            // upsert would leave the old pair behind as a phantom for ever.
            // The binding has no transaction, so a failure between the delete
            // and the last insert leaves the week short until the next run's
            // three-week overlap repairs it.
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
                    "(week_start, site, page_path, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.Site);
                    parameters.Add(row.PagePath);
                    parameters.Add(row.CompanyName);
                    parameters.Add(row.CompanyDomain);
                    parameters.Add(row.Views);
                    parameters.Add(row.UniqueVisitors);
                    parameters.Add(collectionDate);
                }

                await _output.InsertAsync(sqlText, parameters.ToArray());
            }
        }

        internal sealed record PageRow(
            string Site,
            string PagePath,
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dapr-stats.sln`

Expected: PASS, all tests.

- [ ] **Step 5: Wire the activity into the workflow**

In `CollectDaprStats/CollectorWorkflow.cs`, add a third block to the existing
`scarfTasks` section, immediately after the `ScarfLeaderboardPixelIds` block and
before `if (scarfTasks.Count > 0)`:

```csharp
            if (input.ScarfDiagridPagePixelIds?.Length > 0)
            {
                scarfTasks.Add(context.CallActivityAsync(
                    nameof(GetScarfPageViews),
                    new ScarfInput(input.ScarfDiagridPagePixelIds, input.SkipStorage)));
            }
```

Then add the field to the `CollectorWorkflowInput` record at the bottom of the
same file, after `ScarfLeaderboardPixelIds`:

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
        string[] ScarfDiagridPagePixelIds,
        bool SkipStorage);
```

No registration is needed for the activity itself — `AddDaprWorkflow()` is
parameterless and discovers `WorkflowActivity<,>` subclasses automatically.

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build dapr-stats.sln` then `dotnet test dapr-stats.sln`

Expected: build clean, all tests PASS.

- [ ] **Step 7: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/GetScarfPageViews.cs CollectDaprStats/CollectorWorkflow.cs CollectDaprStats.Tests/GetScarfPageViewsTests.cs
git commit -m "feat: collect weekly Diagrid page views per company from Scarf"
```

---

### Task 6: Configuration, request files and documentation

The activity is unreachable until the pixel IDs reach it and the token is in the
environment. All five files below have to move together or a run silently
collects nothing.

**Files:**
- Modify: `dapr.yaml`
- Modify: `.github/workflows/run-workflow.yaml`
- Modify: `local-tests.http`
- Modify: `README.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: `CollectorWorkflowInput.ScarfDiagridPagePixelIds` from Task 5.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Add the secret to the local run configuration**

In `dapr.yaml`, add one line to the `env:` block, after `SCARF_DAPR_API_TOKEN`:

```yaml
      SCARF_DIAGRID_API_TOKEN: "${SCARF_DIAGRID_API_TOKEN}"
```

- [ ] **Step 2: Add the secret and pixel IDs to the GitHub Actions workflow**

In `.github/workflows/run-workflow.yaml`, in the top-level `env:` block, after
the `SCARF_DAPR_API_TOKEN` line:

```yaml
  SCARF_DIAGRID_API_TOKEN: ${{ secrets.SCARF_DIAGRID_API_TOKEN }}
```

In the same block, after `FIXED_SCARF_LEADERBOARD_PIXEL_IDS`:

```yaml
  FIXED_SCARF_DIAGRID_PAGE_PIXEL_IDS: '0a39aa4e-64f2-4851-8a94-2c3417c264ae,030637aa-5521-4cbd-b1b7-3f9f97865a0e'
```

**Do not add a `workflow_dispatch` input.** The file is at GitHub's 10-input
cap; Diagrid collection rides the existing `collect_scarf` checkbox.

In the *Generate Variables and Start CollectorWorkflow* step, extend the
blank-out block so it reads:

```bash
          SCARF_BB_CSV=""
          SCARF_LB_CSV=""
          SCARF_DIAGRID_CSV=""
          ...
          if [ "$COLLECT_SCARF" = "true" ]; then
            SCARF_BB_CSV="$FIXED_SCARF_BUILDINGBLOCK_PIXEL_IDS"
            SCARF_LB_CSV="$FIXED_SCARF_LEADERBOARD_PIXEL_IDS"
            SCARF_DIAGRID_CSV="$FIXED_SCARF_DIAGRID_PAGE_PIXEL_IDS"
          fi
```

and add the field to the `jq` payload — one new `--argjson` line and one new
key:

```bash
            --argjson scarfDiagridPage "$(csv_to_json "$SCARF_DIAGRID_CSV")" \
```

```json
              ScarfDiagridPagePixelIds: $scarfDiagridPage,
```

placed after `ScarfLeaderboardPixelIds` in the object literal.

- [ ] **Step 3: Add the field to every request in `local-tests.http`**

Every payload that carries `ScarfLeaderboardPixelIds` needs the new field
beside it, or the workflow deserialises it as null and the `?.Length > 0` guard
skips Diagrid silently. There are four such payloads: the complete run, the
partial run, the Scarf-only dry run and the Scarf-only storage run.

In the **complete run**, the **Scarf-only dry run** and the **Scarf-only storage
run**, add after the `ScarfLeaderboardPixelIds` line:

```json
    "ScarfDiagridPagePixelIds" : ["0a39aa4e-64f2-4851-8a94-2c3417c264ae", "030637aa-5521-4cbd-b1b7-3f9f97865a0e"],
```

In the **partial run**, whose Scarf lists are empty, add:

```json
    "ScarfDiagridPagePixelIds" : [],
```

Then add a row-count query next to the existing one at the end of the file, for
verifying the two storage runs in Task 7:

```
###
### Diagrid page row counts per week, for verifying the two runs above.
###
POST {{dapr_url}}/v1.0/bindings/daprstats
Content-Type: application/json

{
    "operation": "query",
    "metadata": {
        "sql": "select week_start, count(*) from scarf_diagrid_page_views group by week_start order by week_start"
    }
}

###
### Top Diagrid companies by total page views, most recent complete week.
###
POST {{dapr_url}}/v1.0/bindings/daprstats
Content-Type: application/json

{
    "operation": "query",
    "metadata": {
        "sql": "select company_name, total_views, website_views, docs_views from scarf_diagrid_company_pages_view where week_start = (select max(week_start) from scarf_diagrid_page_views) order by total_views desc limit 25"
    }
}
```

- [ ] **Step 4: Update `README.md`**

In the data-sources list, after the existing Scarf bullet, add:

```markdown
- Scarf pixel data for `diagrid.io` and `docs.diagrid.io`:
  - Weekly views of each page, per company
```

and in the environment-variable list, after `SCARF_DAPR_API_TOKEN`:

```markdown
- `export SCARF_DIAGRID_API_TOKEN=<SCARF_API_TOKEN_VALUE>`
```

In the *Running via GitHub Actions* input table, amend the `collect_scarf` row's
Effect to: `Scarf building block page views and company visits (Dapr), and page
views per company (Diagrid)`.

- [ ] **Step 5: Update `AGENTS.md`**

In the Gotchas list, replace the existing single-account slug bullet with:

```markdown
- **Scarf's owner slug is case-sensitive, and there are now two accounts.**
  `Dapr` and `Diagrid`; the v3 endpoints return `404 Organization not found` for
  a lowercased slug. The slug and the token secret name are no longer consts on
  a shared URL — each activity declares its own `Owner` and `ApiTokenSecret` and
  passes them to `ScarfExportClient`. Diagrid page rows live in
  `scarf_diagrid_page_views`, separate from the two Dapr tables, so neither
  table needs an `account` column yet.
- **`ScarfExportRequest.GroupByArtifact` is `bool?` on purpose.** Omitting
  `group_by_artifact` and sending `group_by_artifact=true` are different
  requests. `GetScarfCompanyViews` sends `false` to merge its two pixels;
  `GetScarfBuildingBlockViews` and `GetScarfPageViews` omit it. Do not collapse
  it to a plain `bool`.
```

- [ ] **Step 6: Verify the Actions payload renders**

Reproduce the payload locally rather than burning an Actions run on a typo. In
a bash shell, paste the `csv_to_json` helper and the `jq -nc` invocation exactly
as they now appear in the workflow file, with the `FIXED_*` values inlined:

```bash
csv_to_json() {
  jq -nc --arg csv "$1" '$csv
    | split(",")
    | map(gsub("^\s+|\s+$"; ""))
    | map(select(length > 0))'
}

csv_to_json '0a39aa4e-64f2-4851-8a94-2c3417c264ae,030637aa-5521-4cbd-b1b7-3f9f97865a0e'
```

Expected: `["0a39aa4e-64f2-4851-8a94-2c3417c264ae","030637aa-5521-4cbd-b1b7-3f9f97865a0e"]`
— a two-element array, not one string containing a comma.

Then confirm the key reached the object literal:

```bash
grep -n 'ScarfDiagridPagePixelIds\|scarfDiagridPage\|SCARF_DIAGRID_CSV\|FIXED_SCARF_DIAGRID' .github/workflows/run-workflow.yaml
```

Expected: five hits — the `FIXED_*` env value, the `SCARF_DIAGRID_CSV=""`
initialiser, the assignment inside the `collect_scarf` branch, the `--argjson`
line, and the `ScarfDiagridPagePixelIds:` key. A missing initialiser is the
dangerous one: `set -euo pipefail` is on, so an unset variable aborts the step.

- [ ] **Step 7: Add the repository secret**

In GitHub → Settings → Secrets and variables → Actions, add
`SCARF_DIAGRID_API_TOKEN` with the Diagrid account's Scarf API token. Without it
the scheduled run will log a Scarf 401 and return `false` for this collector
while everything else still succeeds.

- [ ] **Step 8: Commit** *(ask the repository owner before running any git command)*

```bash
git add dapr.yaml .github/workflows/run-workflow.yaml local-tests.http README.md AGENTS.md
git commit -m "chore: wire Diagrid Scarf page collection into config, requests and docs"
```

---

### Task 7: End-to-end verification

No code changes. This is the gate that says the feature works against the real
Scarf account and the real database.

**Files:** none modified.

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: nothing.

- [ ] **Step 1: Dry run**

```bash
export SCARF_DAPR_API_TOKEN=<dapr token>
export SCARF_DIAGRID_API_TOKEN=<diagrid token>
dapr run -f .
```

Send the "Scarf only, no storage" request from `local-tests.http`.

Expected: three `Scarf Diagrid pages week …` lines, each with a non-zero row,
page and company count. The two Dapr collectors' lines still match the Task 2
baseline. No `WARNING: Scarf Diagrid pages returned zero rows` line — that
means the pixel IDs or the token are wrong, or `ScarfPage` is rejecting every
referer.

- [ ] **Step 2: Sanity-check the page paths before writing anything**

Still in the dry run's output, confirm the paths look like real pages. If
`ScarfPage` were mis-normalising you would see it here as absurd counts or a
single dominant path. Nothing is stored yet, so this is free to repeat.

- [ ] **Step 3: First storage run**

Send the "Scarf only, with storage" request (`SkipStorage: false`), then run the
*Diagrid page row counts per week* query added in Task 6.

Expected: three weeks, each with a non-zero count.

- [ ] **Step 4: Second storage run — the idempotence check**

Send the identical request again, then re-run the row-count query.

Expected: **the counts are unchanged**. This is what proves delete-then-insert
is idempotent. A growing count means the `DELETE` is not matching the rows the
`INSERT` writes — check the `week_start = $1::date` cast.

- [ ] **Step 5: Check the view**

Run the *Top Diagrid companies by total page views* query added in Task 6.

Expected: companies ranked by `total_views`, with `website_views + docs_views =
total_views` on every row. Spot-check the top two or three against the Scarf web
UI for the same week.

- [ ] **Step 6: Confirm the dry-run guard still holds**

Query `select max(collection_date) from scarf_diagrid_page_views`, note it, send
a `SkipStorage: true` Scarf-only run, and query again.

Expected: unchanged. A moved timestamp means an `InsertAsync` escaped the
`SkipStorage` guard.

---

## Notes for the executor

- **Task 2 is the risky one.** It changes two working collectors that feed
  tables with live history. Its Step 1 baseline is not optional — it is the only
  check that the extracted URL is byte-identical in practice rather than just in
  the unit tests.
- **The zero-row guard is load-bearing, not defensive padding.** Scarf answers a
  bad pixel ID with HTTP 200 and an empty array. Without the guard, three weeks
  of history are deleted and not rewritten, and the oldest falls outside the
  next run's window before it could be repaired.
- **Do not "simplify" `GroupByArtifact` to a `bool`.** Task 1's test
  `BuildUrl_NullGroupByArtifact_OmitsTheParameterEntirely` exists to catch that.
- If the row counts come in far above the ~20,000/week the spec estimates,
  stop and report it rather than pressing on — that would suggest `ScarfPage`
  is failing to collapse variants, and the fix belongs in Task 3 before more
  weeks are written.
