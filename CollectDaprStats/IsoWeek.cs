namespace DaprStats
{
    /// <summary>
    /// ISO week arithmetic in UTC. Weeks run Monday 00:00:00Z to the following
    /// Monday 00:00:00Z.
    /// </summary>
    /// <remarks>
    /// Week boundaries are computed here rather than delegated to DataDog's
    /// `interval` bucketing, which is epoch-anchored and therefore produces
    /// Thursday-aligned weeks.
    /// </remarks>
    public static class IsoWeek
    {
        public sealed record Window(DateOnly WeekStart, DateTime From, DateTime To);

        /// <summary>
        /// The <paramref name="count"/> most recently completed ISO weeks,
        /// oldest first. The week containing <paramref name="utcNow"/> is in
        /// progress and is never returned.
        /// </summary>
        public static IReadOnlyList<Window> CompleteWeeksBefore(DateTime utcNow, int count)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "At least one week must be requested.");
            }

            var currentWeekStart = StartOfIsoWeek(DateOnly.FromDateTime(utcNow));

            var windows = new List<Window>(count);
            for (var weeksBack = count; weeksBack >= 1; weeksBack--)
            {
                var weekStart = currentWeekStart.AddDays(-7 * weeksBack);
                windows.Add(new Window(
                    weekStart,
                    MidnightUtc(weekStart),
                    MidnightUtc(weekStart.AddDays(7))));
            }

            return windows;
        }

        private static DateOnly StartOfIsoWeek(DateOnly date)
        {
            // DayOfWeek is Sunday-based (Sunday == 0), ISO weeks are Monday-based.
            var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-daysSinceMonday);
        }

        private static DateTime MidnightUtc(DateOnly date) =>
            DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }
}
