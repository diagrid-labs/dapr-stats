using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfRefererTests
{
    [Theory]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/pubsub-overview/", "pubsub")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/workflow/", "workflow")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/howto-publish-subscribe/", "pubsub")]
    public void TryGetBuildingBlock_CanonicalHost_ReturnsFirstSegmentAfterBuildingBlocks(
        string referer, string expected)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal(expected, block);
    }

    [Theory]
    [InlineData("https://v1-17.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    [InlineData("https://v1-13-1.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    [InlineData("https://v1-9.docs.dapr.io/developing-applications/building-blocks/actors/actors-overview/")]
    public void TryGetBuildingBlock_VersionedDocsHost_IsAccepted(string referer)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal("actors", block);
    }

    [Theory]
    [InlineData("https://dapr.website.cncfstack.com/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://blue-dune-0da9d541e.7.azurestaticapps.net/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://docs.dapr.iox/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://dapr.io/developing-applications/building-blocks/pubsub/")]
    public void TryGetBuildingBlock_ForeignHost_IsRejected(string referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out _));
    }

    [Fact]
    public void TryGetBuildingBlock_QueryStringAndFragment_AreIgnored()
    {
        // 285 rows in the sample week carried ?utm_source= or ?ref=. They must
        // collapse onto the same key as the bare URL.
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/jobs/jobs-overview/?utm_source=chatgpt.com#scheduling",
            out var block));

        Assert.Equal("jobs", block);
    }

    [Fact]
    public void TryGetBuildingBlock_LocalisedPath_YieldsTheSameKeyAsEnglish()
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/zh-hans/developing-applications/building-blocks/state-management/state-management-overview/",
            out var localised));
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/state-management/state-management-overview/",
            out var english));

        Assert.Equal("state-management", english);
        Assert.Equal(english, localised);
    }

    [Theory]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks")]
    [InlineData("https://docs.dapr.io/zh-hans/developing-applications/building-blocks/")]
    public void TryGetBuildingBlock_IndexPage_YieldsTheIndexSentinel(string referer)
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal("(index)", block);
    }

    [Theory]
    [InlineData("https://docs.dapr.io/concepts/overview/")]
    [InlineData("https://docs.dapr.io/")]
    [InlineData("https://docs.dapr.io/reference/cli/dapr-init/")]
    public void TryGetBuildingBlock_NonBuildingBlockPage_IsRejected(string referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/developing-applications/building-blocks/pubsub/")]
    public void TryGetBuildingBlock_UnusableInput_IsRejected(string? referer)
    {
        Assert.False(ScarfReferer.TryGetBuildingBlock(referer, out var block));
        Assert.Equal(string.Empty, block);
    }

    [Fact]
    public void TryGetBuildingBlock_MixedCase_IsLowercased()
    {
        Assert.True(ScarfReferer.TryGetBuildingBlock(
            "https://DOCS.DAPR.IO/developing-applications/building-blocks/PubSub/",
            out var block));

        Assert.Equal("pubsub", block);
    }

    [Fact]
    public void TryGetBuildingBlock_SegmentAtColumnLimit_IsAccepted()
    {
        // building_block is VARCHAR(255) in postgres_schema.psql. Exactly at
        // the limit must still be stored.
        var segment = new string('a', 255);

        Assert.True(ScarfReferer.TryGetBuildingBlock(
            $"https://docs.dapr.io/developing-applications/building-blocks/{segment}/",
            out var block));

        Assert.Equal(segment, block);
    }

    [Fact]
    public void TryGetBuildingBlock_SegmentOverColumnLimit_IsRejected()
    {
        // One character past VARCHAR(255) would otherwise reach Postgres and
        // fail the insert with error 22001.
        var segment = new string('a', 256);

        Assert.False(ScarfReferer.TryGetBuildingBlock(
            $"https://docs.dapr.io/developing-applications/building-blocks/{segment}/",
            out _));
    }

    [Fact]
    public void TryGetBuildingBlock_EscapedSlashInSegment_IsRejected()
    {
        // %2F would otherwise smuggle a literal '/' into the stored value:
        // the segment boundary is chosen before unescaping, so this is not
        // caught by the path-segment split above.
        Assert.False(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/foo%2Fbar/",
            out _));
    }

    [Fact]
    public void TryGetBuildingBlock_EscapedNulInSegment_IsRejected()
    {
        // %00 unescapes to a control character, which Postgres rejects with
        // error 22021.
        Assert.False(ScarfReferer.TryGetBuildingBlock(
            "https://docs.dapr.io/developing-applications/building-blocks/foo%00bar/",
            out _));
    }
}
