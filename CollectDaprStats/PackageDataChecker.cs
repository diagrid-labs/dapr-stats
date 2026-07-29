namespace DaprStats
{
    public class PackageDataChecker
    {
        private readonly PostgresOutput _output;

        public PackageDataChecker(PostgresOutput output)
        {
            _output = output;
        }

        public async Task<string[]> FindMissingAsync(
            string viewName,
            IEnumerable<string> expectedPackageNames,
            DateOnly collectionDate)
        {
            var rows = await _output.ReadAsync($"select package_name, collection_date from {viewName}");

            return FindMissing(rows, expectedPackageNames, collectionDate);
        }

        internal static string[] FindMissing(
            PackageCollectionRecord[] rows,
            IEnumerable<string> expectedPackageNames,
            DateOnly collectionDate)
        {
            var stored = rows
                .Where(r => r.CollectionDate == collectionDate)
                .Select(r => r.PackageName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return expectedPackageNames.Where(name => !stored.Contains(name)).ToArray();
        }

        internal static DateOnly ToCollectionDate(DateTime collectionDate)
        {
            var utc = collectionDate.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(collectionDate, DateTimeKind.Utc)
                : collectionDate.ToUniversalTime();

            return DateOnly.FromDateTime(utc);
        }
    }
}
