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

    [Theory]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("\"oops\"")]
    public void Parse_NonObjectJson_Throws(string nonObjectJson)
    {
        Assert.Throws<FormatException>(() => Parse(nonObjectJson));
    }
}
