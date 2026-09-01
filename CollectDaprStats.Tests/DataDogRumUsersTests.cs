using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumUsersParseIdsTests
{
    private static IReadOnlyList<string> Parse(string json) =>
        DataDogRumUsers.ParseIds(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseIds_ReturnsEveryGroupedId()
    {
        // Shape captured from the live API: one bucket per id, grouped by facet.
        var ids = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": { "@usr.anonymous_id": "aaa" }, "computes": { "c0": 7 } },
                  { "by": { "@usr.anonymous_id": "bbb" }, "computes": { "c0": 2 } }
                ]
              }
            }
            """);

        Assert.Equal(new[] { "aaa", "bbb" }, ids);
    }

    [Fact]
    public void ParseIds_NoBuckets_ReturnsEmpty()
    {
        Assert.Empty(Parse("""{ "data": { "buckets": [] } }"""));
    }

    [Fact]
    public void ParseIds_BucketMissingTheFacet_IsSkippedNotThrown()
    {
        // Sessions with no anonymous id are grouped into a bucket without the
        // facet key. There is no id to register, so skip it.
        var ids = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 1 } },
                  { "by": { "@usr.anonymous_id": "aaa" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Equal(new[] { "aaa" }, ids);
    }

    [Fact]
    public void ParseIds_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumUsers.ParseIds(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseIds_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": {} }"""));
    }
}

public class DataDogRumUsersDeriveEntriesTests
{
    // Real consecutive ISO week starts: Mondays 2026-08-03, 08-10, 08-17, 08-24.
    private static readonly DateOnly[] Mondays =
    [
        new(2026, 8, 3), new(2026, 8, 10), new(2026, 8, 17), new(2026, 8, 24)
    ];

    private static (DateOnly, IReadOnlyList<string>) Week(int index, params string[] ids) =>
        (Mondays[index], ids);

    private static DateOnly? InstallDateOf(
        IReadOnlyList<DataDogRumUsers.Entry> entries, string id) =>
        entries.Single(e => e.AnonymousId == id).InstallDate;

    [Fact]
    public void DeriveEntries_IdInTheOldestWeekWithData_HasNoInstallDate()
    {
        // "aaa" was already active when the window opened, so its real first week
        // is unknowable and must not be guessed.
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "aaa", "bbb")]);

        Assert.Null(InstallDateOf(entries, "aaa"));
    }

    [Fact]
    public void DeriveEntries_IdFirstSeenAfterTheOldestWeek_GetsThatMonday()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "aaa", "bbb"), Week(2, "bbb")]);

        Assert.Equal(new DateOnly(2026, 8, 10), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_IdInManyWeeks_KeepsTheEarliest()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "bbb"), Week(2, "bbb"), Week(3, "bbb")]);

        Assert.Equal(new DateOnly(2026, 8, 10), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_LeadingEmptyWeeksDoNotCountAsTheBoundary()
    {
        // Weeks 0 and 1 returned nothing, so the boundary is week 2 — the oldest
        // week that actually had data.
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0), Week(1), Week(2, "aaa"), Week(3, "bbb")]);

        Assert.Null(InstallDateOf(entries, "aaa"));
        Assert.Equal(new DateOnly(2026, 8, 24), InstallDateOf(entries, "bbb"));
    }

    [Fact]
    public void DeriveEntries_EveryIdAppearsExactlyOnce()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa", "bbb"), Week(1, "aaa", "bbb"), Week(2, "aaa")]);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "aaa", "bbb" }, entries.Select(e => e.AnonymousId).Order());
    }

    [Fact]
    public void DeriveEntries_EveryInstallDateIsAMonday()
    {
        var entries = DataDogRumUsers.DeriveEntries(
            [Week(0, "aaa"), Week(1, "bbb"), Week(2, "ccc")]);

        foreach (var entry in entries.Where(e => e.InstallDate is not null))
        {
            Assert.Equal(DayOfWeek.Monday, entry.InstallDate!.Value.DayOfWeek);
        }
    }

    [Fact]
    public void DeriveEntries_NoDataAtAll_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumUsers.DeriveEntries([Week(0), Week(1)]));
    }
}
