using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumResponseTests
{
    private static (long SessionCount, long UniqueUserCount) Parse(string json) =>
        DataDogRumResponse.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_SingleBucket_ReadsCountThenCardinality()
    {
        // Shape and values captured from the live API for the week of 2026-08-17.
        var (sessions, users) = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 64, "c1": 25 } }
                ]
              }
            }
            """);

        Assert.Equal(64, sessions);
        Assert.Equal(25, users);
    }

    [Fact]
    public void Parse_EmptyBuckets_ReturnsZeroes()
    {
        // A week with no traffic at all. A real zero is worth storing.
        var (sessions, users) = Parse("""
            { "meta": { "status": "done" }, "data": { "buckets": [] } }
            """);

        Assert.Equal(0, sessions);
        Assert.Equal(0, users);
    }

    [Fact]
    public void Parse_FloatingPointComputes_TruncatesToLong()
    {
        // Aggregate computes come back as JSON numbers and are not always integral.
        var (sessions, users) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 64.0, "c1": 25.0 } } ] } }
            """);

        Assert.Equal(64, sessions);
        Assert.Equal(25, users);
    }

    [Fact]
    public void Parse_NullCompute_ReadsAsZero()
    {
        var (sessions, users) = Parse("""
            { "data": { "buckets": [ { "computes": { "c0": 12, "c1": null } } ] } }
            """);

        Assert.Equal(12, sessions);
        Assert.Equal(0, users);
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumResponse.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": { "status": "done" } }"""));
    }

    [Fact]
    public void Parse_NoBucketsArray_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "data": { "buckets": {} } }"""));
    }

    [Fact]
    public void Parse_MissingCompute_Throws()
    {
        Assert.Throws<FormatException>(
            () => Parse("""{ "data": { "buckets": [ { "computes": { "c0": 64 } } ] } }"""));
    }

    [Fact]
    public void Parse_NonNumericCompute_Throws()
    {
        Assert.Throws<FormatException>(
            () => Parse("""{ "data": { "buckets": [ { "computes": { "c0": "64", "c1": 25 } } ] } }"""));
    }
}
