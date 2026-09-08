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
}
