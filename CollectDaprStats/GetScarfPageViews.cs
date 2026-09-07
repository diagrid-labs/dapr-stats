using System.Globalization;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly page views on diagrid.io and docs.diagrid.io, per company, from
    /// the Diagrid Scarf account.
    /// </summary>
    public class GetScarfPageViews : WorkflowActivity<ScarfInput, bool>
    {
        // The same three complete ISO weeks the two Dapr Scarf collectors
        // write. Scarf's company attribution is retroactive — a visitor
        // identified days later changes an earlier week — and the overlap is
        // what lets that correction land.
        private const int WeeksPerRun = 3;

        // Capitalised deliberately: /v3/* returns 404
        // {"detail":"Organization not found"} for anything but `Diagrid`.
        private const string Owner = "Diagrid";
        private const string ApiTokenSecret = "SCARF_DIAGRID_API_TOKEN";

        private const string TableName = "scarf_diagrid_page_views";

        // 1000 x 8 = 8,000 parameters per statement, far inside Postgres'
        // 65,535 limit. Higher than the Dapr collectors' 500 because this
        // table carries an order of magnitude more rows per week — hundreds of
        // pages against hundreds of companies rather than thirteen building
        // blocks.
        private const int RowsPerStatement = 1000;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null, null, null];

        private readonly ScarfExportClient _scarf;
        private readonly PostgresOutput _output;

        public GetScarfPageViews(ScarfExportClient scarf, PostgresOutput output)
        {
            _scarf = scarf;
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            ScarfInput input)
        {
            // One request covers all three weeks: rollup=weekly returns one
            // bucket per week. end_date is exclusive.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            IReadOnlyList<ScarfAggregationResponse.Row> rows;
            try
            {
                rows = await _scarf.FetchAsync(new ScarfExportRequest(
                    Owner,
                    ApiTokenSecret,
                    input.PixelIds,
                    from,
                    to,
                    Breakdown: null,
                    BreakdownSet: "by-referer,by-company",
                    // Omitted, not false. The two Diagrid pixels serve
                    // different hosts, so the (week, referer, company) keys
                    // they produce are already disjoint and there is nothing
                    // to merge server-side.
                    GroupByArtifact: null));
            }
            catch (Exception ex)
            {
                // Not rethrown: this activity runs under Task.WhenAll
                // alongside the two Dapr Scarf activities in
                // CollectorWorkflow, and an unhandled exception here would
                // fail the whole workflow and skip the unrelated GitHub
                // collection below it. The bool return exists so a Scarf
                // outage is reported without taking anything else down.
                Console.WriteLine(
                    $"Failed to fetch Scarf Diagrid page data: {ex.Message}");
                return false;
            }

            var byWeek = Aggregate(rows);

            // Zero rows across every week, from here, is indistinguishable
            // between a genuinely quiet three weeks and two failure modes that
            // both look healthy: Scarf answering an unrecognised
            // tracking_pixel_id with HTTP 200 and {"data":[]}, or a host
            // rename that stops ScarfPage matching any referer. Either would
            // otherwise delete three real weeks of data below and write
            // nothing back, and the three-week window means the oldest of them
            // is gone for good before the next run could repair it. Bail out
            // before the first DELETE rather than risk that.
            if (byWeek.Values.Sum(list => list.Count) == 0)
            {
                Console.WriteLine(
                    "WARNING: Scarf Diagrid pages returned zero rows across " +
                    $"all {WeeksPerRun} weeks. Skipping storage entirely (no " +
                    "deletes performed). Likely causes: a wrong or revoked " +
                    "tracking_pixel_id, or a host change that moved the sites " +
                    "out of ScarfPage's allow-list.");
                return false;
            }

            var allSucceeded = true;

            // Every complete week is written, including one Scarf returned no
            // rows for. A genuinely empty week has to clear the previous
            // collection's rows rather than leave them standing.
            foreach (var week in weeks)
            {
                // Not `: []` — a collection expression in a conditional has no
                // natural type for `var` to infer.
                var weekRows = byWeek.TryGetValue(week.WeekStart, out var found)
                    ? found
                    : new List<PageRow>();

                Console.WriteLine(
                    $"Scarf Diagrid pages week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} rows, " +
                    $"{weekRows.Select(r => (r.Site, r.PagePath)).Distinct().Count()} pages, " +
                    $"{weekRows.Select(r => (r.CompanyName, r.CompanyDomain)).Distinct().Count()} companies");

                if (input.SkipStorage)
                {
                    continue;
                }

                try
                {
                    await StoreAsync(week.WeekStart, weekRows);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to store Scarf Diagrid page views for week " +
                        $"{week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        /// <summary>
        /// Maps referers onto (site, page) pairs and re-sums the result, keyed
        /// by week. Rows whose referer is not a Diagrid page are dropped.
        /// </summary>
        /// <remarks>
        /// Normalisation is many-to-one: query-string variants, `www.`, mixed
        /// case and trailing slashes all collapse onto one key. Without
        /// re-summing here the insert would carry duplicate keys and violate
        /// the unique constraint. Internal so it can be tested.
        /// </remarks>
        internal static Dictionary<DateOnly, List<PageRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Site, string Path, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                if (!ScarfPage.TryGetPage(row.Referer, out var site, out var path))
                {
                    continue;
                }

                var key = (row.WeekStart, site, path, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               running.UniqueVisitors + row.UniqueOrigins);
            }

            var byWeek = new Dictionary<DateOnly, List<PageRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new PageRow(
                    key.Site, key.Path, key.Name, key.Domain,
                    value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<PageRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert rather than upsert: Scarf re-attributing a
            // visitor can remove a (page, company) pair from a week, and an
            // upsert would leave the old pair behind as a phantom for ever.
            // The binding has no transaction, so a failure between the delete
            // and the last insert leaves the week short until the next run's
            // three-week overlap repairs it.
            await _output.InsertAsync(
                $"delete from {TableName} where week_start = $1::date",
                [weekText]);

            if (rows.Count == 0)
            {
                return;
            }

            var collectionDate = DateTime.UtcNow;

            foreach (var chunk in rows.Chunk(RowsPerStatement))
            {
                var sqlText =
                    $"insert into {TableName} " +
                    "(week_start, site, page_path, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.Site);
                    parameters.Add(row.PagePath);
                    parameters.Add(row.CompanyName);
                    parameters.Add(row.CompanyDomain);
                    parameters.Add(row.Views);
                    parameters.Add(row.UniqueVisitors);
                    parameters.Add(collectionDate);
                }

                await _output.InsertAsync(sqlText, parameters.ToArray());
            }
        }

        internal sealed record PageRow(
            string Site,
            string PagePath,
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }
}
