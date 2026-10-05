using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class NuGetSearchTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void SearchEndpoints_ReturnsEachSearchHostOnce()
    {
        // The real index lists both hosts under four SearchQueryService
        // type versions each; querying a host twice would gain nothing.
        var endpoints = NuGetSearch.SearchEndpoints(Utf8("""
        {"version":"3.0.0","resources":[
          {"@id":"https://azuresearch-usnc.nuget.org/query","@type":"SearchQueryService"},
          {"@id":"https://azuresearch-ussc.nuget.org/query","@type":"SearchQueryService"},
          {"@id":"https://azuresearch-usnc.nuget.org/query","@type":"SearchQueryService/3.0.0-rc"},
          {"@id":"https://azuresearch-ussc.nuget.org/query","@type":"SearchQueryService/3.5.0"},
          {"@id":"https://api.nuget.org/v3/registration5-semver1/","@type":"RegistrationsBaseUrl"},
          {"@id":"https://api.nuget.org/v3-flatcontainer/","@type":"PackageBaseAddress/3.0.0"}
        ]}
        """));

        Assert.Equal(
            [new Uri("https://azuresearch-usnc.nuget.org/query"), new Uri("https://azuresearch-ussc.nuget.org/query")],
            endpoints);
    }

    [Fact]
    public void BuildQueryUri_AsksForTheExactPackageStableVersionsOnly()
    {
        // packageid: is an exact-id match; stable-only with semVerLevel=2.0.0 is
        // what the NuGet SDK sent for SearchFilter(false), so the stored
        // version set does not change with the switch away from the SDK.
        Assert.Equal(
            "https://azuresearch-ussc.nuget.org/query?q=packageid:Dapr.Client&prerelease=false&semVerLevel=2.0.0&take=1",
            NuGetSearch.BuildQueryUri(new Uri("https://azuresearch-ussc.nuget.org/query"), "Dapr.Client").ToString());
    }

    [Fact]
    public void ParseSearchResult_ReadsIdAndPerVersionDownloads()
    {
        var result = NuGetSearch.ParseSearchResult(Utf8("""
        {"totalHits":1,"data":[{
          "id":"Dapr.Jobs","version":"1.18.10","totalDownloads":296043,
          "versions":[
            {"version":"1.15.0","downloads":2504,"@id":"https://api.nuget.org/v3/registration5-gz-semver2/dapr.jobs/1.15.0.json"},
            {"version":"1.15.1","downloads":1351,"@id":"https://api.nuget.org/v3/registration5-gz-semver2/dapr.jobs/1.15.1.json"}
          ]
        }]}
        """));

        Assert.NotNull(result);
        Assert.Equal("Dapr.Jobs", result.PackageId);
        Assert.Equal(
            [new NuGetSearch.VersionDownloads("1.15.0", 2504), new NuGetSearch.VersionDownloads("1.15.1", 1351)],
            result.Versions);
    }

    [Fact]
    public void ParseSearchResult_NoHits_ReturnsNull()
    {
        Assert.Null(NuGetSearch.ParseSearchResult(Utf8("""{"totalHits":0,"data":[]}""")));
    }

    [Fact]
    public void PickFreshest_PrefersTheResultWithMoreDownloads()
    {
        // Measured 2026-10-05: azuresearch-usnc had stalled and still served
        // 11,056 downloads for Dapr.Client 1.18.10 while ussc served 33,298.
        // Download counts only grow, so the larger total is the fresher one.
        var stale = new NuGetSearch.SearchResult("Dapr.Client",
            [new("1.18.9", 3224), new("1.18.10", 11056)]);
        var fresh = new NuGetSearch.SearchResult("Dapr.Client",
            [new("1.18.9", 3783), new("1.18.10", 33298)]);

        Assert.Same(fresh, NuGetSearch.PickFreshest([stale, fresh]));
        Assert.Same(fresh, NuGetSearch.PickFreshest([fresh, stale]));
    }

    [Fact]
    public void PickFreshest_SkipsEndpointsThatReturnedNothing()
    {
        // A null is an endpoint that failed or had no hit; the others still count.
        var only = new NuGetSearch.SearchResult("Dapr.Client", [new("1.18.10", 11056)]);

        Assert.Same(only, NuGetSearch.PickFreshest([null, only]));
    }

    [Fact]
    public void PickFreshest_NothingUsable_ReturnsNull()
    {
        Assert.Null(NuGetSearch.PickFreshest([null, null]));
    }
}
