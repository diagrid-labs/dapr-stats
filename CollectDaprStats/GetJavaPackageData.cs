using System.Globalization;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly per-version JVM package downloads from Scarf.
    /// </summary>
    /// <remarks>
    /// Unlike the NuGet, npm and PyPI collectors this makes ONE request for the
    /// whole ecosystem, so it needs no per-package fan-out, no rate-limit
    /// spacing and no Check/retry loop. `query=io.dapr*` returned exactly the
    /// eighteen JVM artifacts on 2026-09-15, excluding the `Dapr CLI` package
    /// that `package_id=all` pulls in.
    /// </remarks>
    public class GetJavaPackageData : WorkflowActivity<JavaPackageInput, bool>
    {
        // `Dapr` is capitalised deliberately: /v3/* returns 404
        // {"detail":"Organization not found"} for anything else.
        private const string Owner = "Dapr";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";

        private const string TableName = "java_dapr";

        // One complete ISO week per run. `int`, not `short`, matching
        // npm's record: the column is SMALLINT and the binding widens.
        private const int CollectedOverNumberOfDays = 7;

        // 500 x 7 = 3,500 parameters per statement, far inside Postgres'
        // 65,535 limit. A week is ~800 rows, so two statements.
        private const int RowsPerStatement = 500;

        // Column order below. Only week_start needs a cast.
        private static readonly string?[] ColumnCasts =
            [null, null, null, null, null, "date", null];

        private static readonly string[] KeyColumns =
            ["week_start", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count", "unique_origins",
             "collected_over_number_of_days"];

        private static readonly string OnConflict =
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

        /// <summary>
        /// The chunked upsert. Internal so the composed string can be asserted
        /// without a database.
        /// </summary>
        internal static string BuildInsertSql(int rowCount) =>
            $"insert into {TableName} (package_name, collection_date, package_version, " +
            "download_count, collected_over_number_of_days, week_start, unique_origins) " +
            $"values {SqlValuesBuilder.Build(rowCount, ColumnCasts)} " +
            OnConflict;

        /// <summary>
        /// Flattens a chunk of rows into the parameter array <see
        /// cref="BuildInsertSql"/>'s placeholders bind against. Extracted so
        /// the order can be asserted directly: two adjacent numeric fields
        /// (<c>row.Downloads</c> and the constant <see
        /// cref="CollectedOverNumberOfDays"/>) would compile fine transposed
        /// and silently corrupt <c>java_dapr</c> in production.
        /// </summary>
        internal static object[] BuildParameters(
            IReadOnlyList<ScarfAggregationResponse.PackageRow> chunk,
            DateTime collectionDate)
        {
            var parameters = new List<object>(chunk.Count * ColumnCasts.Length);

            foreach (var row in chunk)
            {
                parameters.Add(row.PackageName);
                parameters.Add(collectionDate);
                parameters.Add(row.Version);
                parameters.Add(row.Downloads);
                parameters.Add(CollectedOverNumberOfDays);
                // Per row rather than from the requested window: if Scarf
                // ever returns a bucket we did not ask for, label it
                // honestly instead of stamping it with the wrong week.
                parameters.Add(row.WeekStart.ToString(
                    "yyyy-MM-dd", CultureInfo.InvariantCulture));
                parameters.Add(row.UniqueOrigins);
            }

            return parameters.ToArray();
        }

        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetJavaPackageData(ScarfExportClient scarf, PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            JavaPackageInput input)
        {
            // [0] is the older of the two, i.e. the week before last. The most
            // recently completed week is deliberately skipped: on 2026-09-15
            // Scarf's newest ingested event was Saturday the 12th, so that
            // week was ~25% short and would never be revisited.
            var week = IsoWeek.CompleteWeeksBefore(input.CollectionDate, 2)[0];

            IReadOnlyList<ScarfAggregationResponse.PackageRow> rows;
            try
            {
                rows = await _scarf.FetchPackageVersionsAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    [],
                    DateOnly.FromDateTime(week.From),
                    DateOnly.FromDateTime(week.To),
                    Breakdown: null,
                    BreakdownSet: "by-version",
                    GroupByArtifact: null,
                    Query: string.Join(',', input.PackageNames)));
            }
            catch (Exception ex)
            {
                // Not rethrown: this runs under Task.WhenAll alongside the
                // other package collectors, and an unhandled exception would
                // fail the whole workflow and skip everything after it.
                Console.WriteLine($"Failed to fetch Scarf Java data: {ex.Message}");
                return false;
            }

            Console.WriteLine(
                $"Java packages week {week.WeekStart:yyyy-MM-dd}: {rows.Count} rows, " +
                $"{rows.Select(r => r.PackageName).Distinct().Count()} packages");

            if (rows.Count == 0)
            {
                // Nothing is deleted before writing, so an empty response is
                // harmless to stored data — but it always means something is
                // wrong upstream.
                Console.WriteLine(
                    "WARNING: Scarf returned zero Java package rows. Likely " +
                    "causes: a revoked token, or a query pattern that no " +
                    "longer matches any registered package.");
                return false;
            }

            if (input.SkipStorage)
            {
                return true;
            }

            foreach (var chunk in rows.Chunk(RowsPerStatement))
            {
                await _output.InsertAsync(
                    BuildInsertSql(chunk.Length),
                    BuildParameters(chunk, input.CollectionDate));
            }

            return true;
        }
    }

    public record JavaPackageInput(
        string[] PackageNames,
        DateTime CollectionDate,
        bool SkipStorage);
}
