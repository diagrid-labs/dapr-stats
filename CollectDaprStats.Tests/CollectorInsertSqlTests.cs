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
