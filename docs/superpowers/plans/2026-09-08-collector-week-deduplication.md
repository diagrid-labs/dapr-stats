# Collector week deduplication — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a second CollectorWorkflow run in the same ISO week update the stored measurement instead of appending a duplicate row, for the seven sources that have no such guard.

**Architecture:** Each of the seven tables gains a generated `collection_week` column (`date_trunc('week', collection_date)::date`) and a `UNIQUE` constraint over `(collection_week, <entity key>)`. A new `UpsertBuilder` generates the `on conflict ... do update set ...` tail; each collector's insert SQL moves to an `internal static readonly` field so the composed statement can be asserted in a unit test.

**Tech Stack:** .NET 10, Dapr Workflow, xUnit 2.9.2, PostgreSQL 16 (Neon), Dapr PostgreSQL output binding.

**Spec:** `docs/superpowers/specs/2026-09-08-collector-week-deduplication-design.md`

## Global Constraints

- Target framework is `net10.0`. Tests run with `dotnet test`.
- The Dapr binding takes **one statement and one flat parameter array per invocation**. All seven collectors write **one row per statement**. Do not introduce chunking.
- `collection_week` is `GENERATED ALWAYS ... STORED`. It is **never** in an `INSERT` column list and **never** in an `on conflict do update set` list.
- Neon's HTTP `/sql` endpoint takes **one statement per request** and cannot hold a transaction open. Migration files are written for the Neon SQL editor.
- Every DDL statement is dry-run inside an aborting `DO` block before being applied. `P0001 DRY RUN OK` means pass; any other SQLSTATE is a real failure.
- Constraint naming follows the existing convention: `<table>_week_unique`.
- Collectors keep their existing per-source exception handling and `bool` return. Do not convert a caught failure into a thrown one.
- `CollectDaprStats.Tests` already reaches internals via `InternalsVisibleTo` in `CollectDaprStats.csproj:20`. No new attribute needed.

---

## File Structure

**Create:**
- `CollectDaprStats/UpsertBuilder.cs` — generates the `on conflict` tail. Pure string building, no I/O.
- `CollectDaprStats.Tests/UpsertBuilderTests.cs` — three clause shapes and four rejections.
- `CollectDaprStats.Tests/CollectorInsertSqlTests.cs` — one golden-string assertion per collector.
- `postgres/add-collector-week-dedup-2026-09-08.psql` — the migration.

**Modify:**
- `CollectDaprStats/GetDockerHubData.cs`, `GetDiscordData.cs`, `GetDiagridDashboardData.cs`, `GetPythonPackageData.cs`, `GetNuGetPackageData.cs`, `GetNpmPackageData.cs` — extract insert SQL to a static field, append the upsert tail.
- `CollectDaprStats/GetGitHubRepoData.cs` — same, twelve update columns.
- `postgres/postgres_schema.psql` — record the new columns and constraints.
- `README.md` — state that collection is idempotent per ISO week.

---

### Task 1: `UpsertBuilder`

**Files:**
- Create: `CollectDaprStats/UpsertBuilder.cs`
- Test: `CollectDaprStats.Tests/UpsertBuilderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string UpsertBuilder.BuildOnConflict(IReadOnlyList<string> keyColumns, IReadOnlyList<string> updateColumns)`. Returns a clause with **no leading or trailing space**, lowercase, comma-separated with no spaces after commas — matching `SqlValuesBuilder`'s style. Throws `ArgumentException` on an empty list, on `collection_week` appearing in `updateColumns`, and on any column appearing in both lists.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/UpsertBuilderTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

public class UpsertBuilderTests
{
    [Fact]
    public void BuildOnConflict_MultiColumnKey_ListsKeysThenAssignments()
    {
        Assert.Equal(
            "on conflict (collection_week,namespace,image_name) do update set " +
            "collection_date=excluded.collection_date,pull_count=excluded.pull_count",
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "namespace", "image_name"],
                ["collection_date", "pull_count"]));
    }

    [Fact]
    public void BuildOnConflict_SingleColumnKey_OmitsCommaInKeyList()
    {
        // discord_dapr and diagrid_dashboard have no entity dimension: one row
        // per week, so collection_week is the whole key.
        Assert.Equal(
            "on conflict (collection_week) do update set " +
            "collection_date=excluded.collection_date,member_count=excluded.member_count",
            UpsertBuilder.BuildOnConflict(
                ["collection_week"],
                ["collection_date", "member_count"]));
    }

    [Fact]
    public void BuildOnConflict_PreservesGivenColumnOrder()
    {
        // Order is preserved rather than sorted, so the golden-string tests in
        // CollectorInsertSqlTests are stable against this builder.
        Assert.Equal(
            "on conflict (collection_week,b,a) do update set z=excluded.z,y=excluded.y",
            UpsertBuilder.BuildOnConflict(["collection_week", "b", "a"], ["z", "y"]));
    }

    [Fact]
    public void BuildOnConflict_EmptyKeyColumns_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict([], ["collection_date"]));

        Assert.Equal("keyColumns", ex.ParamName);
    }

    [Fact]
    public void BuildOnConflict_EmptyUpdateColumns_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(["collection_week"], []));

        Assert.Equal("updateColumns", ex.ParamName);
    }

    [Fact]
    public void BuildOnConflict_GeneratedColumnInUpdates_Throws()
    {
        // collection_week is GENERATED ALWAYS. Postgres would reject the
        // assignment at runtime; failing here is cheaper. Checked before the
        // key-overlap rule so the message names the real problem even when
        // collection_week is also in the key, which it normally is.
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "namespace"],
                ["collection_week", "pull_count"]));

        Assert.Equal("updateColumns", ex.ParamName);
        Assert.Contains("generated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOnConflict_KeyColumnAlsoInUpdates_Throws()
    {
        // Updating a conflict-key column would change the very value that
        // matched, so it is always a mistake.
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "repo_name"],
                ["repo_name", "commit_count"]));

        Assert.Equal("updateColumns", ex.ParamName);
        Assert.Contains("repo_name", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test CollectDaprStats.Tests --filter UpsertBuilderTests`

Expected: FAIL to **compile** — `error CS0103: The name 'UpsertBuilder' does not exist`. A compile failure is the correct red state here; there is no type yet.

- [ ] **Step 3: Write the implementation**

Create `CollectDaprStats/UpsertBuilder.cs`:

```csharp
using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Builds the `on conflict (a,b) do update set c=excluded.c` tail that makes
    /// an INSERT idempotent against its unique constraint.
    /// </summary>
    /// <remarks>
    /// Separate and tested for the same reason as <see cref="SqlValuesBuilder"/>:
    /// the clause is generated text, and a transposed column — `set
    /// commit_count=excluded.comment_count` — would corrupt data silently and
    /// permanently. `github_dapr` alone has twelve update columns.
    /// </remarks>
    public static class UpsertBuilder
    {
        /// <summary>
        /// The generated column every one of these constraints leads with. It is
        /// computed by Postgres from `collection_date` and cannot be assigned.
        /// </summary>
        private const string GeneratedColumn = "collection_week";

        /// <param name="keyColumns">
        /// The conflict target, in index order. Must match an existing unique
        /// constraint or Postgres raises 42P10.
        /// </param>
        /// <param name="updateColumns">
        /// The columns to overwrite with the incoming row's values. Excludes the
        /// key columns and <see cref="GeneratedColumn"/>.
        /// </param>
        public static string BuildOnConflict(
            IReadOnlyList<string> keyColumns,
            IReadOnlyList<string> updateColumns)
        {
            if (keyColumns.Count == 0)
            {
                throw new ArgumentException(
                    "At least one key column is required.", nameof(keyColumns));
            }

            if (updateColumns.Count == 0)
            {
                throw new ArgumentException(
                    "At least one update column is required.", nameof(updateColumns));
            }

            foreach (var column in updateColumns)
            {
                // Checked before the overlap rule below: collection_week is
                // normally in the key as well, and this is the more useful
                // message of the two.
                if (string.Equals(column, GeneratedColumn, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"'{GeneratedColumn}' is a generated column and cannot be assigned.",
                        nameof(updateColumns));
                }

                if (keyColumns.Contains(column, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Column '{column}' is part of the conflict key and must not be updated.",
                        nameof(updateColumns));
                }
            }

            var builder = new StringBuilder("on conflict (");
            builder.AppendJoin(',', keyColumns);
            builder.Append(") do update set ");

            for (var i = 0; i < updateColumns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(updateColumns[i])
                       .Append("=excluded.")
                       .Append(updateColumns[i]);
            }

            return builder.ToString();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CollectDaprStats.Tests --filter UpsertBuilderTests`

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/UpsertBuilder.cs CollectDaprStats.Tests/UpsertBuilderTests.cs
git commit -m "feat: add UpsertBuilder for idempotent collector inserts"
```

---

### Task 2: The six narrow collectors

**Files:**
- Modify: `CollectDaprStats/GetDockerHubData.cs:41-42`
- Modify: `CollectDaprStats/GetDiscordData.cs:45-46`
- Modify: `CollectDaprStats/GetDiagridDashboardData.cs:71-72`
- Modify: `CollectDaprStats/GetPythonPackageData.cs:39-40`
- Modify: `CollectDaprStats/GetNuGetPackageData.cs:45-46`
- Modify: `CollectDaprStats/GetNpmPackageData.cs:43-44`
- Test: `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`

**Interfaces:**
- Consumes: `UpsertBuilder.BuildOnConflict(IReadOnlyList<string>, IReadOnlyList<string>)` from Task 1.
- Produces: `internal static readonly string InsertSql` on each of `GetDockerHubData`, `GetDiscordData`, `GetDiagridDashboardData`, `GetPythonPackageData`, `GetNuGetPackageData`, `GetNpmPackageData`.

**CRITICAL — static initialiser order.** `InsertSql` reads `KeyColumns` and `UpdateColumns`, and C# runs static field initialisers in **textual declaration order**. Declare the two arrays **above** `InsertSql` in every collector. Getting this wrong yields a `NullReferenceException` on first use, at collection time, in production.

- [ ] **Step 1: Write the failing tests**

Create `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`:

```csharp
using DaprStats;

namespace CollectDaprStats.Tests;

/// <summary>
/// Golden strings for every collector's composed INSERT. These are the tests
/// that catch a transposed EXCLUDED column — the failure that motivated a
/// shared builder over seven hand-written clauses.
/// </summary>
public class CollectorInsertSqlTests
{
    [Fact]
    public void DockerHub_InsertSql_UpsertsOnWeekAndImage()
    {
        Assert.Equal(
            "insert into dockerhub_images (namespace, image_name, collection_date, pull_count) " +
            "values ($1, $2, $3, $4) " +
            "on conflict (collection_week,namespace,image_name) do update set " +
            "collection_date=excluded.collection_date,pull_count=excluded.pull_count",
            GetDockerHubData.InsertSql);
    }

    [Fact]
    public void Discord_InsertSql_UpsertsOnWeekAlone()
    {
        Assert.Equal(
            "insert into discord_dapr (collection_date, member_count) " +
            "values ($1, $2) " +
            "on conflict (collection_week) do update set " +
            "collection_date=excluded.collection_date,member_count=excluded.member_count",
            GetDiscordData.InsertSql);
    }

    [Fact]
    public void DiagridDashboard_InsertSql_UpsertsOnWeekAlone()
    {
        Assert.Equal(
            "insert into diagrid_dashboard (collection_date, download_count) " +
            "values ($1, $2) " +
            "on conflict (collection_week) do update set " +
            "collection_date=excluded.collection_date,download_count=excluded.download_count",
            GetDiagridDashboardData.InsertSql);
    }

    [Fact]
    public void Python_InsertSql_KeyExcludesVersionBecauseItIsAlwaysAll()
    {
        // python_dapr stores one row per package with PackageVersion "all", so
        // package_version is an updatable attribute, not part of the key.
        Assert.Equal(
            "insert into python_dapr (package_name, collection_date, package_version, download_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5) " +
            "on conflict (collection_week,package_name) do update set " +
            "collection_date=excluded.collection_date," +
            "package_version=excluded.package_version," +
            "download_count=excluded.download_count," +
            "collected_over_number_of_days=excluded.collected_over_number_of_days",
            GetPythonPackageData.InsertSql);
    }

    [Fact]
    public void NuGet_InsertSql_KeyIncludesVersion()
    {
        // nuget_dapr_client stores one row per (package, version) — ~248 rows
        // per run — so package_version is part of the key.
        Assert.Equal(
            "insert into nuget_dapr_client (package_name, collection_date, package_version, download_count) " +
            "values ($1, $2, $3, $4) " +
            "on conflict (collection_week,package_name,package_version) do update set " +
            "collection_date=excluded.collection_date,download_count=excluded.download_count",
            GetNuGetPackageData.InsertSql);
    }

    [Fact]
    public void Npm_InsertSql_KeyIncludesVersion()
    {
        Assert.Equal(
            "insert into npm_dapr_dapr (package_name, collection_date, package_version, download_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5) " +
            "on conflict (collection_week,package_name,package_version) do update set " +
            "collection_date=excluded.collection_date," +
            "download_count=excluded.download_count," +
            "collected_over_number_of_days=excluded.collected_over_number_of_days",
            GetNpmPackageData.InsertSql);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test CollectDaprStats.Tests --filter CollectorInsertSqlTests`

Expected: FAIL to compile — `error CS0117: 'GetDockerHubData' does not contain a definition for 'InsertSql'`, once per collector.

- [ ] **Step 3: `GetDockerHubData`**

In `CollectDaprStats/GetDockerHubData.cs`, add these members to the class body, above `RunAsync`:

```csharp
        private const string TableName = "dockerhub_images";

        // Declared above InsertSql: static initialisers run in textual order.
        private static readonly string[] KeyColumns =
            ["collection_week", "namespace", "image_name"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "pull_count"];

        /// <summary>
        /// Internal so the composed statement can be asserted without a database.
        /// </summary>
        internal static readonly string InsertSql =
            $"insert into {TableName} (namespace, image_name, collection_date, pull_count) " +
            "values ($1, $2, $3, $4) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Then replace lines 41-42 — the `const string tableName` and `var sqlText` declarations — so the storage block reads:

```csharp
                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { dockerHubImageData.Namespace, dockerHubImageData.ImageName, dockerHubImageData.CollectionDate, dockerHubImageData.PullCount };
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }
```

- [ ] **Step 4: `GetDiscordData`**

Add to the class body, above `RunAsync`:

```csharp
        private const string TableName = "discord_dapr";

        private static readonly string[] KeyColumns = ["collection_week"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "member_count"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (collection_date, member_count) " +
            "values ($1, $2) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Replace lines 45-46 so the storage block reads:

```csharp
            if (!input.SkipStorage)
            {
                var sqlParameters = new object[] { data.CollectionDate, data.MemberCount };
                await _output.InsertAsync(InsertSql, sqlParameters);
            }
```

- [ ] **Step 5: `GetDiagridDashboardData`**

Add to the class body, above `RunAsync`:

```csharp
        private const string TableName = "diagrid_dashboard";

        private static readonly string[] KeyColumns = ["collection_week"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (collection_date, download_count) " +
            "values ($1, $2) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Replace lines 71-72 so the storage block reads:

```csharp
                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { data.CollectionDate, data.DownloadCount };
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }
```

- [ ] **Step 6: `GetPythonPackageData`**

Add to the class body, above `RunAsync`:

```csharp
        private const string TableName = "python_dapr";

        // package_version is NOT in the key: this collector always stores the
        // literal "all", so (collection_week, package_name) identifies the row.
        private static readonly string[] KeyColumns =
            ["collection_week", "package_name"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "package_version", "download_count",
             "collected_over_number_of_days"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Replace lines 39-40 so the storage block reads:

```csharp
                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { pythonPackageData.PackageName, pythonPackageData.CollectionDate, pythonPackageData.PackageVersion, pythonPackageData.Downloads, pythonPackageData.CollectedOverNumberOfDays };
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }
```

- [ ] **Step 7: `GetNuGetPackageData`**

Add to the class body, above `RunAsync`:

```csharp
        private const string TableName = "nuget_dapr_client";

        private static readonly string[] KeyColumns =
            ["collection_week", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (package_name, collection_date, package_version, download_count) " +
            "values ($1, $2, $3, $4) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Replace lines 45-46. The insert stays inside the `foreach (var version in daprClientVersions)` loop — one statement per version, unchanged:

```csharp
                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { nugetPackageVersionData.PackageName, nugetPackageVersionData.CollectionDate, nugetPackageVersionData.PackageVersion, nugetPackageVersionData.Downloads};
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }
```

- [ ] **Step 8: `GetNpmPackageData`**

Add to the class body, above `RunAsync`:

```csharp
        private const string TableName = "npm_dapr_dapr";

        private static readonly string[] KeyColumns =
            ["collection_week", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count", "collected_over_number_of_days"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Replace lines 43-44. The insert stays inside the `foreach (var versionPair in ...)` loop:

```csharp
                    if (!input.SkipStorage)
                    {
                        var sqlParameters = new object[] { npmPackageVersionData.PackageName, npmPackageVersionData.CollectionDate, npmPackageVersionData.PackageVersion, npmPackageVersionData.Downloads, npmPackageVersionData.CollectedOverNumberOfDays };
                        await _output.InsertAsync(InsertSql, sqlParameters);
                    }
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test CollectDaprStats.Tests --filter CollectorInsertSqlTests`

Expected: PASS, 6 tests.

Then run the whole suite to confirm nothing regressed:

Run: `dotnet test`

Expected: PASS. No existing test touches these collectors' SQL, so the count should rise by exactly the 13 tests added in Tasks 1-2.

- [ ] **Step 10: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/GetDockerHubData.cs CollectDaprStats/GetDiscordData.cs \
        CollectDaprStats/GetDiagridDashboardData.cs CollectDaprStats/GetPythonPackageData.cs \
        CollectDaprStats/GetNuGetPackageData.cs CollectDaprStats/GetNpmPackageData.cs \
        CollectDaprStats.Tests/CollectorInsertSqlTests.cs
git commit -m "feat: upsert per ISO week in the six narrow collectors"
```

---

### Task 3: `GetGitHubRepoData` — the twelve-column upsert

Separated from Task 2 because this is where a transposed `EXCLUDED` column is most likely and most damaging: twelve update columns, four of them `*_count` and four `*_users`, with similar names (`commit_users` / `comment_users`, `commit_count` / `comment_count`).

**Files:**
- Modify: `CollectDaprStats/GetGitHubRepoData.cs:59-60`
- Test: `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`

**Interfaces:**
- Consumes: `UpsertBuilder.BuildOnConflict` from Task 1.
- Produces: `internal static readonly string GetGitHubRepoData.InsertSql`.

- [ ] **Step 1: Write the failing test**

Append to `CollectDaprStats.Tests/CollectorInsertSqlTests.cs`:

```csharp
    [Fact]
    public void GitHub_InsertSql_UpdatesAllTwelveMeasurementColumns()
    {
        // Written out in full rather than generated, so a transposition between
        // the similarly named pairs — commit_users/comment_users,
        // commit_count/comment_count — fails here rather than silently storing
        // one column's value in another for the rest of the table's life.
        Assert.Equal(
            "insert into github_dapr (repo_name, collection_date, fork_count_total, star_count_total, commit_count, commit_users, issue_count, issue_users, comment_count, comment_users, pullrequest_count, pullrequest_users, distinct_user_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14) " +
            "on conflict (collection_week,repo_name) do update set " +
            "collection_date=excluded.collection_date," +
            "fork_count_total=excluded.fork_count_total," +
            "star_count_total=excluded.star_count_total," +
            "commit_count=excluded.commit_count," +
            "commit_users=excluded.commit_users," +
            "issue_count=excluded.issue_count," +
            "issue_users=excluded.issue_users," +
            "comment_count=excluded.comment_count," +
            "comment_users=excluded.comment_users," +
            "pullrequest_count=excluded.pullrequest_count," +
            "pullrequest_users=excluded.pullrequest_users," +
            "distinct_user_count=excluded.distinct_user_count," +
            "collected_over_number_of_days=excluded.collected_over_number_of_days",
            GetGitHubRepoData.InsertSql);
    }

    [Fact]
    public void GitHub_UpdateColumns_MatchTheInsertColumnListExactlyMinusTheKey()
    {
        // Guards the gap this pair of lists can develop: a column added to the
        // INSERT but forgotten in the update list would be written on first
        // insert and then never refreshed on a re-run.
        var inserted = GetGitHubRepoData.InsertSql
            .Split("(")[1].Split(")")[0]
            .Split(',', StringSplitOptions.TrimEntries);

        var updated = GetGitHubRepoData.InsertSql
            .Split(" do update set ")[1]
            .Split(',')
            .Select(assignment => assignment.Split('=')[0])
            .ToArray();

        // repo_name is the only inserted column that is part of the key.
        Assert.Equal(inserted.Where(c => c != "repo_name").Order(), updated.Order());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test CollectDaprStats.Tests --filter CollectorInsertSqlTests`

Expected: FAIL to compile — `error CS0117: 'GetGitHubRepoData' does not contain a definition for 'InsertSql'`.

- [ ] **Step 3: Write the implementation**

Add to the `GetGitHubRepoData` class body, above `RunAsync`:

```csharp
        private const string TableName = "github_dapr";

        private static readonly string[] KeyColumns =
            ["collection_week", "repo_name"];

        // Every column in the INSERT except repo_name, which is the key. A
        // re-run must refresh all of them: the whole point is that running the
        // workflow again repairs a partially failed collection.
        private static readonly string[] UpdateColumns =
            ["collection_date", "fork_count_total", "star_count_total",
             "commit_count", "commit_users", "issue_count", "issue_users",
             "comment_count", "comment_users", "pullrequest_count",
             "pullrequest_users", "distinct_user_count",
             "collected_over_number_of_days"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (repo_name, collection_date, fork_count_total, star_count_total, commit_count, commit_users, issue_count, issue_users, comment_count, comment_users, pullrequest_count, pullrequest_users, distinct_user_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);
```

Then delete lines 59-60 — `string tableName = $"github_dapr";` and the `var sqlText` line, the former being a pointless interpolation — leaving:

```csharp
                var sqlParameters = new object[] { githubData.Repository, githubData.CollectionDate, githubData.ForksTotalCount, githubData.StarsTotalCount, githubData.CommitCount, githubData.CommitUsers, githubData.IssueCount, githubData.IssueUsers, githubData.CommentCount, githubData.CommentUsers, githubData.PullRequestCount, githubData.PullRequestUsers, githubData.DistinctUserCount, githubData.CollectedOverNumberOfDays };
                await _output.InsertAsync(InsertSql, sqlParameters);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`

Expected: PASS. 15 tests added across Tasks 1-3.

- [ ] **Step 5: Commit** *(ask the repository owner before running any git command)*

```bash
git add CollectDaprStats/GetGitHubRepoData.cs CollectDaprStats.Tests/CollectorInsertSqlTests.cs
git commit -m "feat: upsert per ISO week in the GitHub collector"
```

---

### Task 4: Migration script and documentation

Writes and **dry-runs** the migration. Applying it is Task 5, because applying it without deploying the code in the same sitting leaves a window where the old code raises a unique violation on a second same-week run.

**Files:**
- Create: `postgres/add-collector-week-dedup-2026-09-08.psql`
- Modify: `postgres/postgres_schema.psql`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: the seven `collection_week` columns and seven `<table>_week_unique` constraints that Tasks 2-3's `on conflict` targets require. Without them Postgres raises `42P10 there is no unique or exclusion constraint matching the ON CONFLICT specification`.

- [ ] **Step 1: Write the migration script**

Create `postgres/add-collector-week-dedup-2026-09-08.psql`:

```sql
-- Add per-ISO-week deduplication to the seven collector tables that have no
-- unique constraint, so a second CollectorWorkflow run in the same week
-- updates the stored measurement instead of appending a duplicate row.
--
-- Design: docs/superpowers/specs/2026-09-08-collector-week-deduplication-design.md
-- Plan:   docs/superpowers/plans/2026-09-08-collector-week-deduplication.md
--
-- WHY: duplicates happened on 2026-09-01 and again on 2026-09-07. The trigger
-- is GitHub's scheduler, not operator error - scheduled runs on this workflow
-- have been delayed by 2h, by 19h39m, and once by 8 days. A delayed run looks
-- skipped, someone dispatches manually, and the delayed run lands anyway.
--
-- collection_week is GENERATED ALWAYS ... STORED, so it cannot drift from
-- collection_date and the application never writes it. Existing rows backfill
-- on ADD COLUMN - no UPDATE and no SET NOT NULL step.
--
-- collection_week is deliberately NOT called week_start. In scarf_* and
-- datadog_rum* that name means "the week this data describes"; here it means
-- only "the week the run happened in". A Monday run's GitHub numbers mostly
-- describe the PREVIOUS week, so joining these tables to
-- scarf_company_views.week_start would be off by roughly a week.
--
-- Neon's HTTP /sql endpoint takes ONE STATEMENT PER REQUEST. Run this in the
-- Neon SQL editor.
--
-- Statement 1 DELETES A ROW. Everything after it is additive.


-- 1. Resolve the one historical collision. python_dapr holds two rows for
--    `dapr` in ISO week 2026-07-27 and the unique index cannot be built until
--    one goes:
--
--      id 152  2026-07-29 13:23:32  358,005 downloads   <- DELETE (partial retry run)
--      id 153  2026-08-01 10:25:09  406,398 downloads   <- KEEP (full run, and
--                                                          the later of the two,
--                                                          matching last-write-wins)
--
--    Verify these two rows are what you expect before deleting.

SELECT id, package_name, collection_date, download_count
FROM python_dapr
WHERE (date_trunc('week', collection_date))::date = DATE '2026-07-27'
ORDER BY collection_date;

DELETE FROM python_dapr WHERE id = 152;


-- 2. The seven columns and seven constraints. Each pair is independent; a
--    failure part-way leaves the earlier tables migrated and the later ones
--    untouched, which is safe - the code in Tasks 2-3 is not deployed yet.

ALTER TABLE nuget_dapr_client
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE nuget_dapr_client
  ADD CONSTRAINT nuget_dapr_client_week_unique
  UNIQUE (collection_week, package_name, package_version);

ALTER TABLE github_dapr
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE github_dapr
  ADD CONSTRAINT github_dapr_week_unique
  UNIQUE (collection_week, repo_name);

ALTER TABLE npm_dapr_dapr
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE npm_dapr_dapr
  ADD CONSTRAINT npm_dapr_dapr_week_unique
  UNIQUE (collection_week, package_name, package_version);

ALTER TABLE python_dapr
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE python_dapr
  ADD CONSTRAINT python_dapr_week_unique
  UNIQUE (collection_week, package_name);

ALTER TABLE dockerhub_images
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE dockerhub_images
  ADD CONSTRAINT dockerhub_images_week_unique
  UNIQUE (collection_week, namespace, image_name);

ALTER TABLE discord_dapr
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE discord_dapr
  ADD CONSTRAINT discord_dapr_week_unique
  UNIQUE (collection_week);

ALTER TABLE diagrid_dashboard
  ADD COLUMN collection_week date
  GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
ALTER TABLE diagrid_dashboard
  ADD CONSTRAINT diagrid_dashboard_week_unique
  UNIQUE (collection_week);


-- 3. Verify. Expect 7 columns and 7 constraints.

SELECT table_name, column_name, is_generated, generation_expression
FROM information_schema.columns
WHERE table_schema = 'public' AND column_name = 'collection_week'
ORDER BY table_name;

SELECT tc.table_name, tc.constraint_name,
       string_agg(kcu.column_name, ', ' ORDER BY kcu.ordinal_position) AS key_columns
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu
  ON kcu.constraint_name = tc.constraint_name
 AND kcu.table_schema = tc.table_schema
WHERE tc.table_schema = 'public'
  AND tc.constraint_name LIKE '%\_week\_unique'
GROUP BY tc.table_name, tc.constraint_name
ORDER BY tc.table_name;


-- 4. Confirm no table has more than one row per week per entity.
--    Expect zero rows.

WITH k AS (
  SELECT 'nuget_dapr_client' t, collection_week wk, package_name || '@' || package_version key FROM nuget_dapr_client
  UNION ALL SELECT 'github_dapr', collection_week, repo_name FROM github_dapr
  UNION ALL SELECT 'npm_dapr_dapr', collection_week, package_name || '@' || package_version FROM npm_dapr_dapr
  UNION ALL SELECT 'python_dapr', collection_week, package_name FROM python_dapr
  UNION ALL SELECT 'dockerhub_images', collection_week, namespace || '/' || image_name FROM dockerhub_images
  UNION ALL SELECT 'discord_dapr', collection_week, '(single)' FROM discord_dapr
  UNION ALL SELECT 'diagrid_dashboard', collection_week, '(single)' FROM diagrid_dashboard
)
SELECT t AS table_name, wk AS collection_week, key, count(*) AS rows
FROM k GROUP BY 1, 2, 3 HAVING count(*) > 1 ORDER BY 1, 2;
```

- [ ] **Step 2: Dry-run the DDL against Neon**

Run each of the two blocks below through Neon's HTTP `/sql` endpoint or the SQL editor. The `DO` block raises at the end, so the whole statement aborts and nothing persists — the technique used for every migration in this repo.

First, confirm the collision is still exactly one row and that the constraint is genuinely blocked:

```sql
DO $$
BEGIN
  ALTER TABLE python_dapr ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE python_dapr ADD CONSTRAINT python_dapr_week_unique
    UNIQUE (collection_week, package_name);
  RAISE EXCEPTION 'UNEXPECTED: constraint added without resolving id 152';
END $$;
```

Expected: `23505 Key (collection_week, package_name)=(2026-07-27, dapr) is duplicated.`
If instead you get `P0001 UNEXPECTED`, someone has already resolved the row — re-check step 1 before deleting anything.

Then dry-run the whole migration, collision resolved, in one aborting block:

```sql
DO $$
DECLARE cols int; cons int;
BEGIN
  DELETE FROM python_dapr WHERE id = 152;

  ALTER TABLE nuget_dapr_client ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE nuget_dapr_client ADD CONSTRAINT nuget_dapr_client_week_unique
    UNIQUE (collection_week, package_name, package_version);

  ALTER TABLE github_dapr ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE github_dapr ADD CONSTRAINT github_dapr_week_unique
    UNIQUE (collection_week, repo_name);

  ALTER TABLE npm_dapr_dapr ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE npm_dapr_dapr ADD CONSTRAINT npm_dapr_dapr_week_unique
    UNIQUE (collection_week, package_name, package_version);

  ALTER TABLE python_dapr ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE python_dapr ADD CONSTRAINT python_dapr_week_unique
    UNIQUE (collection_week, package_name);

  ALTER TABLE dockerhub_images ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE dockerhub_images ADD CONSTRAINT dockerhub_images_week_unique
    UNIQUE (collection_week, namespace, image_name);

  ALTER TABLE discord_dapr ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE discord_dapr ADD CONSTRAINT discord_dapr_week_unique
    UNIQUE (collection_week);

  ALTER TABLE diagrid_dashboard ADD COLUMN collection_week date
    GENERATED ALWAYS AS ((date_trunc('week', collection_date))::date) STORED;
  ALTER TABLE diagrid_dashboard ADD CONSTRAINT diagrid_dashboard_week_unique
    UNIQUE (collection_week);

  SELECT count(*) INTO cols FROM information_schema.columns
   WHERE table_schema='public' AND column_name='collection_week';
  SELECT count(*) INTO cons FROM information_schema.table_constraints
   WHERE table_schema='public' AND constraint_name LIKE '%\_week\_unique';

  RAISE EXCEPTION 'DRY RUN OK | collection_week columns=% | week_unique constraints=%',
    cols, cons;
END $$;
```

Expected: `P0001 DRY RUN OK | collection_week columns=7 | week_unique constraints=9`.

Nine, not seven: `datadog_rum_service_week_unique` and `datadog_rum_identified_week_unique` already exist and match the `LIKE` pattern. If you see 7, the two DataDog constraints have gone missing and that is a separate problem.

- [ ] **Step 3: Confirm nothing persisted**

```sql
SELECT count(*) AS should_be_zero
FROM information_schema.columns
WHERE table_schema = 'public' AND column_name = 'collection_week';
```

Expected: `0`. Also confirm `SELECT count(*) FROM python_dapr` is still `145` — the dry run's `DELETE` must have rolled back.

- [ ] **Step 4: Record the schema in `postgres/postgres_schema.psql`**

Append the fourteen statements from step 1's section 2 to the end of the file, preceded by this comment:

```sql
-- Per-ISO-week deduplication for the seven collector tables that previously
-- had no unique constraint.
--
-- NOT YET APPLIED as of 2026-09-08. This records the DDL that
-- postgres/add-collector-week-dedup-2026-09-08.psql will apply; Task 5 step 7
-- replaces this paragraph with "Applied <date>" once the migration has run.
-- Do not write "Applied" here before it is true: this is the file people read
-- to learn the current schema, and it is the only such annotation in it.
--
-- collection_week is GENERATED, so the application never writes it and it
-- cannot drift from collection_date.
--
-- NOTE: collection_week is NOT week_start. In scarf_* and datadog_rum* that
-- name means "the week this data describes". Here it means only "the week the
-- run happened in" - a deduplication slot. A Monday run's github_dapr numbers
-- mostly describe the previous week, so joining these tables to
-- scarf_company_views.week_start would be off by roughly a week.
```

- [ ] **Step 5: Update `README.md`**

In the "Running via GitHub Actions" section, replace the bullet beginning **"Type `none` to skip an ecosystem — do not clear the field."** — keep its text, and add this bullet directly after it:

```markdown
- **Re-running is safe, and is how you repair a bad run.** Every source
  deduplicates on the ISO week the run happened in, so collecting twice on the
  same day, or twice in the same week, updates the stored numbers rather than
  duplicating them. A run that half-failed — GitHub throttled some repos,
  pypistats rate-limited a package — is fixed by simply running it again. This
  matters because GitHub delays scheduled runs: this workflow's have been late
  by two hours, by nineteen, and once by eight days, and a delayed run that
  lands after a manual one used to double every row it wrote.
```

- [ ] **Step 6: Commit** *(ask the repository owner before running any git command)*

```bash
git add postgres/add-collector-week-dedup-2026-09-08.psql postgres/postgres_schema.psql README.md
git commit -m "feat: migration for per-ISO-week collector deduplication"
```

---

### Task 5: Apply and verify

There is no separate deployment step: `.github/workflows/run-workflow.yaml:108-129` does `actions/checkout@v4` → `dotnet build` → `dapr run`, so **merging to main is the deploy** — the next run simply builds whatever main holds.

That also means a manual *Run workflow* can be pointed at the feature branch, so the new code can be proved against the migrated database **before** merging. Order:

1. Apply the migration.
2. Run the workflow twice from the feature branch and verify.
3. Merge.

Between steps 1 and 3, main still holds the old code, which would raise `23505` on a second same-week run. The schedule is weekly on Monday, so **do this on a non-Monday** and the window is uneventful. The reverse order is worse: merging first makes every insert fail with `42P10 there is no unique or exclusion constraint matching the ON CONFLICT specification`.

**Files:** none — this task runs SQL and the workflow.

**Interfaces:**
- Consumes: the migration file from Task 4 and the collector changes from Tasks 2-3.
- Produces: nothing in code.

- [ ] **Step 1: Confirm the code is ready**

Run: `dotnet test`

Expected: PASS, all tests. Do not apply the migration if anything fails.

- [ ] **Step 2: Apply the migration**

Run `postgres/add-collector-week-dedup-2026-09-08.psql` in the Neon SQL editor, statement by statement. Confirm as you go:

- Step 1's `SELECT` shows ids 152 and 153 with the download counts in the header comment. **If it shows anything else, stop.**
- The `DELETE` reports 1 row.
- The fourteen `ALTER` statements all succeed.
- Section 3's verification shows 7 columns, each with `is_generated = ALWAYS`, and 9 `%_week_unique` constraints (7 new + 2 pre-existing DataDog).
- Section 4 returns zero rows.

- [ ] **Step 3: Prove idempotency with a real double run, from the branch**

In the Actions tab, choose *Run CollectorWorkflow* → *Run workflow*, and set **Use workflow from** to `feat/collector-week-deduplication` rather than `main`. Leave every input at its default. Run it, wait for it to finish, then run it a second time the same way.

Both runs land in the same ISO week, which is exactly the case that used to duplicate. Capture the counts before the second run and after it:

```sql
SELECT 'nuget_dapr_client' t, count(*) n FROM nuget_dapr_client
UNION ALL SELECT 'github_dapr', count(*) FROM github_dapr
UNION ALL SELECT 'npm_dapr_dapr', count(*) FROM npm_dapr_dapr
UNION ALL SELECT 'python_dapr', count(*) FROM python_dapr
UNION ALL SELECT 'dockerhub_images', count(*) FROM dockerhub_images
UNION ALL SELECT 'discord_dapr', count(*) FROM discord_dapr
UNION ALL SELECT 'diagrid_dashboard', count(*) FROM diagrid_dashboard
ORDER BY 1;
```

Expected: **identical counts before and after the second run.** Every table flat.

- [ ] **Step 4: Confirm the second run actually wrote**

Flat counts alone would also be consistent with the second run storing nothing at all, so check that the timestamps moved:

```sql
SELECT collection_week, count(*) AS rows,
       min(collection_date) AS first_written,
       max(collection_date) AS last_written
FROM github_dapr
WHERE collection_week = (date_trunc('week', now()))::date
GROUP BY collection_week;
```

Expected: `rows` = the repo count (45 as of 2026-09-08), and `last_written` at the **second** run's time. That combination — one row per repo, timestamp from the later run — is the whole feature.

- [ ] **Step 5: Confirm the delta views are not distorted**

```sql
SELECT namespace, image_name, collection_date, days_diff, avg_weekly_diff
FROM dockerhub_images_view
WHERE image_name = 'daprd'
ORDER BY collection_date DESC
LIMIT 3;
```

Expected: `days_diff` in the 6-8 range, `avg_weekly_diff` in the tens of thousands. A `days_diff` near zero with an `avg_weekly_diff` in the hundreds of thousands would mean two rows survived in one week and the constraint is not doing its job. Healthy history for reference: `days_diff` 6.3-23.1, `avg_weekly_diff` 41k-51k.

- [ ] **Step 6: Record the outcome in BOTH files**

First, add an `APPLIED <date> ... COMPLETE AND VERIFIED` block to the header of `postgres/add-collector-week-dedup-2026-09-08.psql`, following the convention in `postgres/remove-duplicate-collection-2026-09-07.psql` and `postgres/add-user-orgs-link-table-2026-09-03.psql`: the verification numbers, the row counts before and after the double run, and confirmation that the DataDog and Scarf tables were untouched.

Then — **do not skip this** — flip the annotation in `postgres/postgres_schema.psql`. It currently reads `NOT YET APPLIED as of 2026-09-08 ...`; replace that paragraph with `Applied <date>; see postgres/add-collector-week-dedup-2026-09-08.psql.` Until you do, the file that documents the current schema understates what is actually in the database. This is the mirror of the earlier hazard: before Task 5 a false "Applied" misleads, and after Task 5 a stale "NOT YET APPLIED" misleads just as much.

- [ ] **Step 7: Commit** *(ask the repository owner before running any git command)*

```bash
git add postgres/add-collector-week-dedup-2026-09-08.psql
git commit -m "docs: record the applied collector deduplication migration"
```

- [ ] **Step 8: Merge to main** *(ask the repository owner before running any git command)*

Merging is the deploy: the next scheduled run checks out main and builds it. Do this only once steps 3-5 have passed against the migrated database, so main never holds code the schema cannot serve.

```bash
gh pr create --fill --base main
```

Until this merge lands, main still holds the pre-upsert code, which raises `23505` rather than duplicating if it runs twice in one week. That is the safe direction, but it is why steps 2-8 belong in one sitting on a non-Monday.
