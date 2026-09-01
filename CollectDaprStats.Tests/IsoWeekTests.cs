using DaprStats;

namespace CollectDaprStats.Tests;

public class IsoWeekTests
{
    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string[] WeekStarts(DateTime utcNow, int count) =>
        IsoWeek.CompleteWeeksBefore(utcNow, count)
            .Select(w => w.WeekStart.ToString("yyyy-MM-dd"))
            .ToArray();

    [Fact]
    public void CompleteWeeksBefore_Midweek_ReturnsThreeCompleteWeeksOldestFirst()
    {
        // Wednesday. The week starting Mon 2026-08-24 is in progress and excluded.
        var weeks = WeekStarts(Utc("2026-08-26T12:00:00Z"), 3);

        Assert.Equal(new[] { "2026-08-03", "2026-08-10", "2026-08-17" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_ExactlyMondayMidnight_ExcludesTheWeekJustStarted()
    {
        // The week 08-17..08-24 has only just completed, so it is the newest one.
        var weeks = WeekStarts(Utc("2026-08-24T00:00:00Z"), 3);

        Assert.Equal(new[] { "2026-08-03", "2026-08-10", "2026-08-17" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_LateSunday_StillExcludesTheCurrentWeek()
    {
        // Sunday 23:59:59Z. DayOfWeek.Sunday is 0, which is the case a naive
        // (int)DayOfWeek - 1 calculation gets wrong.
        var weeks = WeekStarts(Utc("2026-08-23T23:59:59Z"), 3);

        Assert.Equal(new[] { "2026-07-27", "2026-08-03", "2026-08-10" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_AcrossYearBoundary_WalksBackIntoThePreviousYear()
    {
        // Friday 2027-01-01. Its ISO week starts Mon 2026-12-28.
        var weeks = WeekStarts(Utc("2027-01-01T09:00:00Z"), 3);

        Assert.Equal(new[] { "2026-12-07", "2026-12-14", "2026-12-21" }, weeks);
    }

    [Fact]
    public void CompleteWeeksBefore_WindowsAreContiguousSevenDayUtcSpans()
    {
        var weeks = IsoWeek.CompleteWeeksBefore(Utc("2026-08-26T12:00:00Z"), 3);

        foreach (var week in weeks)
        {
            Assert.Equal(DateTimeKind.Utc, week.From.Kind);
            Assert.Equal(DateTimeKind.Utc, week.To.Kind);
            Assert.Equal(TimeSpan.FromDays(7), week.To - week.From);
            Assert.Equal(week.WeekStart, DateOnly.FromDateTime(week.From));
            Assert.Equal(DayOfWeek.Monday, week.From.DayOfWeek);
        }

        Assert.Equal(weeks[0].To, weeks[1].From);
        Assert.Equal(weeks[1].To, weeks[2].From);
    }

    [Fact]
    public void CompleteWeeksBefore_NewestWindowNeverOverlapsTheCurrentWeek()
    {
        var utcNow = Utc("2026-08-26T12:00:00Z");

        var newest = IsoWeek.CompleteWeeksBefore(utcNow, 3).Last();

        Assert.True(newest.To <= utcNow);
    }

    [Fact]
    public void CompleteWeeksBefore_CountBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IsoWeek.CompleteWeeksBefore(Utc("2026-08-26T12:00:00Z"), 0));
    }
}
