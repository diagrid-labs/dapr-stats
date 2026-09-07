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
