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
        // Company domains deliberately differ from what Row()'s convention
        // would produce, and both are asserted explicitly: a mutant that
        // dropped CompanyDomain from the key tuple, or that swapped
        // Name/Domain when constructing PageRow, would still pass a bare
        // count assertion.
        var rows = GetScarfPageViews.Aggregate([
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", "Acme", "acme.com", 1, 1),
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", "Globex", "globex.org", 2, 1),
        ]);

        Assert.Equal(2, rows[Week].Count);

        var acme = Assert.Single(rows[Week], r => r.CompanyName == "Acme");
        Assert.Equal("acme.com", acme.CompanyDomain);
        Assert.Equal(1, acme.Views);

        var globex = Assert.Single(rows[Week], r => r.CompanyName == "Globex");
        Assert.Equal("globex.org", globex.CompanyDomain);
        Assert.Equal(2, globex.Views);
    }

    [Fact]
    public void Aggregate_SameCompanyNameDifferentDomain_StayDistinct()
    {
        // Pins CompanyDomain into the key tuple specifically: two rows that
        // share CompanyName but differ only in CompanyDomain must not
        // collapse into one, which a key tuple missing Domain would allow.
        var rows = GetScarfPageViews.Aggregate([
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", "Acme", "acme.com", 1, 1),
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", "Acme", "acme.io", 2, 1),
        ]);

        Assert.Equal(2, rows[Week].Count);

        var dotCom = Assert.Single(rows[Week], r => r.CompanyDomain == "acme.com");
        Assert.Equal(1, dotCom.Views);

        var dotIo = Assert.Single(rows[Week], r => r.CompanyDomain == "acme.io");
        Assert.Equal(2, dotIo.Views);
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

    [Fact]
    public void Aggregate_CompanyNameOverLengthLimit_IsDropped()
    {
        // 256 characters, one over the company_name VARCHAR(255) column.
        // Well under the byte budget on its own, so this isolates the
        // character-length check from the byte-budget check below.
        var overLongName = new string('a', 256);

        var rows = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/pricing", overLongName, 1, 1),
        ]);

        Assert.Empty(rows);
    }

    [Fact]
    public void Aggregate_KeyOverByteBudget_IsDropped()
    {
        // company_name and company_domain are each exactly 255 multi-byte
        // characters ('あ' is 3 UTF-8 bytes), so each passes its own
        // character-length check individually, but together with the page
        // path they push the combined UTF-8 key past the 2000-byte budget.
        var multiByteCompanyField = new string('あ', 255);
        var multiBytePathSegment = new string('あ', 200);
        var referer = $"https://diagrid.io/{Uri.EscapeDataString(multiBytePathSegment)}";

        var rows = GetScarfPageViews.Aggregate([
            new ScarfAggregationResponse.Row(
                Week, referer, multiByteCompanyField, multiByteCompanyField, 1, 1),
        ]);

        Assert.Empty(rows);
    }

    [Fact]
    public void Aggregate_LargeButLegalKey_IsRetained()
    {
        // Both company fields sit exactly at the 255-character limit, but
        // stay ASCII, so the combined UTF-8 key stays comfortably under the
        // 2000-byte budget and the row must survive.
        var companyName = new string('a', 255);
        var companyDomain = new string('b', 255);

        var rows = GetScarfPageViews.Aggregate([
            new ScarfAggregationResponse.Row(
                Week, "https://diagrid.io/pricing", companyName, companyDomain, 3, 2),
        ]);

        var page = Assert.Single(rows[Week]);
        Assert.Equal(companyName, page.CompanyName);
        Assert.Equal(companyDomain, page.CompanyDomain);
        Assert.Equal(3, page.Views);
        Assert.Equal(2, page.UniqueVisitors);
    }

    [Fact]
    public void MissingSites_BothSitesPresent_ReturnsEmpty()
    {
        var byWeek = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/pricing", "Acme", 1, 1),
            Row("https://docs.diagrid.io/catalyst", "Acme", 1, 1),
        ]);

        Assert.Empty(GetScarfPageViews.MissingSites(byWeek));
    }

    [Fact]
    public void MissingSites_OnlyWebsitePresent_ReportsDocsMissing()
    {
        var byWeek = GetScarfPageViews.Aggregate([
            Row("https://diagrid.io/pricing", "Acme", 1, 1),
        ]);

        var missing = GetScarfPageViews.MissingSites(byWeek);

        Assert.Equal(["docs.diagrid.io"], missing);
    }

    [Fact]
    public void MissingSites_OnlyDocsPresent_ReportsWebsiteMissing()
    {
        var byWeek = GetScarfPageViews.Aggregate([
            Row("https://docs.diagrid.io/catalyst", "Acme", 1, 1),
        ]);

        var missing = GetScarfPageViews.MissingSites(byWeek);

        Assert.Equal(["diagrid.io"], missing);
    }

    [Fact]
    public void MissingSites_NeitherSitePresent_ReportsBothMissing()
    {
        // An empty dictionary is exactly what Aggregate returns when every
        // referer is unresolvable, so this doubles as the old all-sites-empty
        // case the previous guard covered.
        var byWeek = new Dictionary<DateOnly, List<GetScarfPageViews.PageRow>>();

        var missing = GetScarfPageViews.MissingSites(byWeek);

        Assert.Equal(["diagrid.io", "docs.diagrid.io"], missing);
    }
}
