using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class PostgresOutputParseRowsTests
{
    private static PackageCollectionRecord[] Parse(string json) =>
        PostgresOutput.ParseRows(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseRows_MapsColumnsByPosition()
    {
        var rows = Parse("""[["dapr","2026-07-27T00:00:00Z"],["dapr-agents","2026-07-27T00:00:00Z"]]""");

        Assert.Equal(2, rows.Length);
        Assert.Equal("dapr", rows[0].PackageName);
        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
        Assert.Equal("dapr-agents", rows[1].PackageName);
    }

    [Fact]
    public void ParseRows_PlainDateString_ParsesCalendarDate()
    {
        var rows = Parse("""[["dapr","2026-07-27"]]""");

        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_UtcTimestamp_KeepsSameCalendarDate()
    {
        var rows = Parse("""[["dapr","2026-07-27T00:00:00Z"]]""");

        Assert.Equal(new DateOnly(2026, 7, 27), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_OffsetTimestamp_NormalizesToUtcBeforeTakingDate()
    {
        // 2026-07-27T00:00:00+02:00 is 2026-07-26T22:00:00Z.
        // This fails if the parser uses the host's local timezone instead of UTC.
        var rows = Parse("""[["dapr","2026-07-27T00:00:00+02:00"]]""");

        Assert.Equal(new DateOnly(2026, 7, 26), rows[0].CollectionDate);
    }

    [Fact]
    public void ParseRows_EmptyPayload_ReturnsEmpty()
    {
        Assert.Empty(PostgresOutput.ParseRows(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseRows_NullLiteral_ReturnsEmpty()
    {
        Assert.Empty(Parse("null"));
    }

    [Fact]
    public void ParseRows_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(Parse("[]"));
    }
}
