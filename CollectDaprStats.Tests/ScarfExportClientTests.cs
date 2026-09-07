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
