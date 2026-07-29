using DaprStats;

namespace CollectDaprStats.Tests;

public class PackageDataCheckerTests
{
    private static readonly DateOnly Today = new(2026, 7, 27);

    private static PackageCollectionRecord Row(string name, DateOnly? date = null) =>
        new(name, date ?? Today);

    [Fact]
    public void FindMissing_AllPackagesStored_ReturnsEmpty()
    {
        var rows = new[] { Row("dapr"), Row("dapr-agents") };

        var missing = PackageDataChecker.FindMissing(rows, ["dapr", "dapr-agents"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_SomePackagesStored_ReturnsOnlyTheMissingOnes()
    {
        var rows = new[] { Row("dapr") };

        var missing = PackageDataChecker.FindMissing(
            rows, ["dapr", "dapr-agents", "dapr-ext-workflow"], Today);

        Assert.Equal(new[] { "dapr-agents", "dapr-ext-workflow" }, missing);
    }

    [Fact]
    public void FindMissing_ViewEmpty_ReturnsAllExpected()
    {
        var missing = PackageDataChecker.FindMissing([], ["dapr", "dapr-agents"], Today);

        Assert.Equal(new[] { "dapr", "dapr-agents" }, missing);
    }

    [Fact]
    public void FindMissing_RowsFromAnEarlierCollectionDate_ReturnsAllExpected()
    {
        // The views select MAX(collection_date), so a run that stored nothing today
        // still returns yesterday's rows. Those must not count as stored.
        var rows = new[] { Row("dapr", Today.AddDays(-1)), Row("dapr-agents", Today.AddDays(-1)) };

        var missing = PackageDataChecker.FindMissing(rows, ["dapr", "dapr-agents"], Today);

        Assert.Equal(new[] { "dapr", "dapr-agents" }, missing);
    }

    [Fact]
    public void FindMissing_StoredNameDiffersOnlyByCase_CountsAsStored()
    {
        var rows = new[] { Row("dapr.client") };

        var missing = PackageDataChecker.FindMissing(rows, ["Dapr.Client"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_DuplicateRowsForSamePackage_CountsAsStored()
    {
        var rows = new[] { Row("@dapr/dapr"), Row("@dapr/dapr") };

        var missing = PackageDataChecker.FindMissing(rows, ["@dapr/dapr"], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissing_NothingExpected_ReturnsEmpty()
    {
        var rows = new[] { Row("dapr") };

        var missing = PackageDataChecker.FindMissing(rows, [], Today);

        Assert.Empty(missing);
    }

    [Fact]
    public void ToCollectionDate_UtcInput_KeepsCalendarDate()
    {
        var utc = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 7, 27), PackageDataChecker.ToCollectionDate(utc));
    }

    [Fact]
    public void ToCollectionDate_UnspecifiedKind_IsTreatedAsUtc()
    {
        // JSON without a trailing Z deserializes to DateTimeKind.Unspecified.
        // Calling ToUniversalTime on it would treat it as local and can shift the day.
        var unspecified = new DateTime(2026, 7, 27, 1, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateOnly(2026, 7, 27), PackageDataChecker.ToCollectionDate(unspecified));
    }
}
