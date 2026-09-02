using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumIdentifiedResponseTests
{
    private static (long SessionCount, long UniqueUserCount, long OrgCount) Parse(string json) =>
        DataDogRumIdentifiedResponse.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_ReadsAllThreeComputesPositionally()
    {
        // Computes are keyed by request order, so c0 is the count, c1 the
        // @usr.id cardinality and c2 the @usr.organization cardinality. A
        // reordered request body would silently swap these.
        var (sessions, users, orgs) = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "computes": { "c0": 91, "c1": 27, "c2": 11 } }
                ]
              }
            }
            """);

        Assert.Equal(91, sessions);
        Assert.Equal(27, users);
        Assert.Equal(11, orgs);
    }

    [Fact]
    public void Parse_NoBuckets_IsARealZeroWeek()
    {
        // No session matched the query. That is a genuine zero, not an error --
        // dapr-ops-dashboard has weeks like this.
        Assert.Equal((0L, 0L, 0L), Parse("""{ "data": { "buckets": [] } }"""));
    }

    [Fact]
    public void Parse_NonIntegralNumbers_AreTruncatedToLong()
    {
        // Cardinality is an estimate and does not always arrive integral.
        var (sessions, users, orgs) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 91.0, "c1": 27.4, "c2": 11.9 } } ] } }
            """);

        Assert.Equal(91, sessions);
        Assert.Equal(27, users);
        Assert.Equal(11, orgs);
    }

    [Fact]
    public void Parse_NullCompute_IsZero()
    {
        var (_, _, orgs) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 5, "c1": 2, "c2": null } } ] } }
            """);

        Assert.Equal(0, orgs);
    }

    [Fact]
    public void Parse_MissingOrgCompute_Throws()
    {
        // The org compute silently missing would store 0 orgs for a week that
        // had some, so this must fail loudly rather than default.
        Assert.Throws<FormatException>(() => Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 5, "c1": 2 } } ] } }
            """));
    }

    [Fact]
    public void Parse_BucketWithoutComputes_Throws()
    {
        Assert.Throws<FormatException>(
            () => Parse("""{ "data": { "buckets": [ { } ] } }"""));
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumIdentifiedResponse.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("not json"));
    }

    [Fact]
    public void Parse_NoDataBucketsArray_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": {} }"""));
    }

    [Fact]
    public void Parse_ComputeThatIsNotANumber_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 5, "c1": 2, "c2": "eleven" } } ] } }
            """));
    }
}
