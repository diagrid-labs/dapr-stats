using DaprStats;

namespace CollectDaprStats.Tests;

public class ScarfPageTests
{
    [Theory]
    [InlineData("https://docs.diagrid.io/catalyst/concepts/pubsub/", "docs.diagrid.io", "/catalyst/concepts/pubsub")]
    [InlineData("https://diagrid.io/pricing", "diagrid.io", "/pricing")]
    [InlineData("https://www.diagrid.io/pricing", "diagrid.io", "/pricing")]
    [InlineData("https://DOCS.DIAGRID.IO/catalyst/", "docs.diagrid.io", "/catalyst")]
    public void TryGetPage_AllowedHosts_AreAcceptedAndNormalised(
        string referer, string expectedSite, string expectedPath)
    {
        Assert.True(ScarfPage.TryGetPage(referer, out var site, out var path));
        Assert.Equal(expectedSite, site);
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("https://dapr.io/")]
    [InlineData("https://docs.dapr.io/developing-applications/building-blocks/pubsub/")]
    [InlineData("https://diagrid.iox/pricing")]
    [InlineData("https://staging.diagrid.io/pricing")]
    [InlineData("https://docs.diagrid.io.evil.com/pricing")]
    [InlineData("http://localhost:1313/pricing")]
    public void TryGetPage_ForeignHosts_AreRejected(string referer)
    {
        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/pricing")]
    [InlineData("not a url")]
    public void TryGetPage_MissingOrRelativeReferer_IsRejected(string? referer)
    {
        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }

    [Fact]
    public void TryGetPage_QueryStringAndFragment_AreIgnored()
    {
        // Tracking parameters must collapse onto the same key as the bare URL,
        // or one page fragments into a dozen rows.
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/pricing?utm_source=chatgpt.com&ref=x#plans",
            out var site, out var path));

        Assert.Equal("diagrid.io", site);
        Assert.Equal("/pricing", path);
    }

    [Fact]
    public void TryGetPage_MixedCasePath_IsLowercased()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/Pricing/Enterprise", out _, out var path));

        Assert.Equal("/pricing/enterprise", path);
    }

    [Fact]
    public void TryGetPage_RepeatedSlashes_AreCollapsed()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://docs.diagrid.io//catalyst///concepts/", out _, out var path));

        Assert.Equal("/catalyst/concepts", path);
    }

    [Theory]
    [InlineData("https://diagrid.io/")]
    [InlineData("https://diagrid.io")]
    [InlineData("https://www.diagrid.io/")]
    public void TryGetPage_RootPath_IsStoredAsASingleSlash(string referer)
    {
        Assert.True(ScarfPage.TryGetPage(referer, out _, out var path));
        Assert.Equal("/", path);
    }

    [Fact]
    public void TryGetPage_PercentEncodedSlash_IsUnescapedIntoTheStoredPath()
    {
        // Unlike ScarfReferer there is no key extraction here for a %2F to
        // smuggle a delimiter past: the whole path is the key. Unescaping is
        // what makes /my%20page and "/my page" one row rather than two.
        Assert.True(ScarfPage.TryGetPage(
            "https://docs.diagrid.io/catalyst%2Fconcepts", out _, out var path));

        Assert.Equal("/catalyst/concepts", path);
    }

    [Fact]
    public void TryGetPage_PercentEncodedSpace_IsUnescaped()
    {
        Assert.True(ScarfPage.TryGetPage(
            "https://diagrid.io/my%20page", out _, out var path));

        Assert.Equal("/my page", path);
    }

    [Fact]
    public void TryGetPage_ControlCharacters_AreRejected()
    {
        Assert.False(ScarfPage.TryGetPage(
            "https://diagrid.io/pri%00cing", out _, out _));
    }

    [Fact]
    public void TryGetPage_PathAtTheColumnWidth_IsAccepted()
    {
        // 512 characters: "/" plus 511. Matches page_path VARCHAR(512).
        var referer = "https://diagrid.io/" + new string('a', 511);

        Assert.True(ScarfPage.TryGetPage(referer, out _, out var path));
        Assert.Equal(512, path.Length);
    }

    [Fact]
    public void TryGetPage_PathOverTheColumnWidth_IsRejected()
    {
        // Rejected rather than truncated: an over-long value would fail its
        // whole insert chunk with Postgres 22001 *after* the week's DELETE had
        // already run, losing the week.
        var referer = "https://diagrid.io/" + new string('a', 512);

        Assert.False(ScarfPage.TryGetPage(referer, out _, out _));
    }
}
