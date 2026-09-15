using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfAggregationResponseTests
{
    private static IReadOnlyList<ScarfAggregationResponse.Row> Parse(string json) =>
        ScarfAggregationResponse.Parse(Encoding.UTF8.GetBytes(json));

    private static IReadOnlyList<ScarfAggregationResponse.PackageRow> ParsePackages(
        string json) =>
        ScarfAggregationResponse.ParsePackageVersions(Encoding.UTF8.GetBytes(json));

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

    [Theory]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("\"oops\"")]
    public void Parse_NonObjectJson_Throws(string nonObjectJson)
    {
        Assert.Throws<FormatException>(() => Parse(nonObjectJson));
    }

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
}
