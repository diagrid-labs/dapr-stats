using System.Globalization;
using System.Text;
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

        // company_name and company_domain are VARCHAR(255), which Postgres
        // measures in characters. A value over this fails the whole insert
        // chunk with error 22001 after the week's DELETE has already run, and
        // the same company/domain returns in every subsequent Scarf export,
        // so the week never self-heals. Checked in characters to match the
        // column definition.
        private const int MaxCompanyFieldLength = 255;

        // The unique index over (week_start, site, page_path, company_name,
        // company_domain) has a btree index-row limit of roughly 2704 bytes,
        // measured in BYTES, while the VARCHAR limits above are measured in
        // CHARACTERS. A worst-case UTF-8 key can reach ~3.1KB and exceed the
        // index limit despite every field passing its own character check.
        // 2000 leaves comfortable margin under 2704.
        private const int MaxKeyByteLength = 2000;

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

            // Zero rows for a site, from here, is indistinguishable between a
            // genuinely quiet three weeks for that site and two failure modes
            // that both look healthy: Scarf answering an unrecognised
            // tracking_pixel_id with HTTP 200 and {"data":[]}, or a host
            // rename that stops ScarfPage matching any referer for that site.
            // This request carries two pixel IDs, one per site, and Scarf
            // answers each independently — a bad or rotated docs.diagrid.io
            // pixel does not stop diagrid.io rows from coming back, so a
            // guard that only checks the combined total stays silent while
            // one site's rows quietly disappear.
            //
            // Two situations need opposite handling here:
            //
            // - Every site is missing: there is nothing healthy to write, and
            //   a missing site's stored rows must never be deleted, so bail
            //   out before the first DELETE.
            // - Some sites are missing and some are not: treat the missing
            //   site's pixel as broken or revoked. Leave its stored rows
            //   standing — do not run its DELETE for any of the three weeks
            //   — and continue storing the healthy sites normally, including
            //   their per-week DELETE for weeks where a healthy site
            //   genuinely had no rows (a quiet week, not a missing site).
            var missing = MissingSites(byWeek);

            if (missing.Length == ScarfPage.Sites.Length)
            {
                // Every site empty: same unrecoverable case the guard has
                // always covered. Bail before the first DELETE.
                Console.WriteLine(
                    "WARNING: Scarf Diagrid pages returned zero rows for every site " +
                    $"across all {WeeksPerRun} weeks. Skipping storage entirely (no " +
                    "deletes performed). Likely causes: a wrong or revoked API token, " +
                    "or wrong tracking_pixel_ids.");
                return false;
            }

            var healthySites = HealthySites(missing);
            var allSucceeded = missing.Length == 0;

            if (missing.Length > 0)
            {
                Console.WriteLine(
                    $"WARNING: Scarf Diagrid pages returned zero rows for " +
                    $"{string.Join(", ", missing)} across all {WeeksPerRun} weeks. " +
                    "Leaving the stored rows for those sites untouched and " +
                    $"continuing with {string.Join(", ", healthySites)}. Likely causes: " +
                    "a wrong or revoked tracking_pixel_id, or a host change that moved " +
                    "a site out of ScarfPage's allow-list.");
            }

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
                    await StoreAsync(week.WeekStart, weekRows, healthySites);
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

                if (!IsStorableKey(row.WeekStart, site, path, row.CompanyName, row.CompanyDomain))
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

        /// <summary>
        /// The <see cref="ScarfPage.Sites"/> entries with no rows anywhere in
        /// <paramref name="byWeek"/>. Internal so the zero-row guard's
        /// predicate can be tested without going through <see cref="RunAsync"/>.
        /// </summary>
        internal static string[] MissingSites(Dictionary<DateOnly, List<PageRow>> byWeek)
        {
            var sitesSeen = byWeek.Values.SelectMany(rows => rows)
                                         .Select(row => row.Site)
                                         .ToHashSet(StringComparer.Ordinal);

            return ScarfPage.Sites.Where(site => !sitesSeen.Contains(site)).ToArray();
        }

        /// <summary>
        /// The sites whose rows may be rewritten this run: every known site except
        /// those that produced nothing across the whole window.
        /// </summary>
        /// <remarks>
        /// This complement is the single step that stops a broken pixel's history
        /// being deleted, which is why it is extracted and tested rather than
        /// written inline.
        /// </remarks>
        internal static string[] HealthySites(string[] missing) =>
            ScarfPage.Sites.Where(site => !missing.Contains(site)).ToArray();

        /// <summary>
        /// Guards the unique key's storability, in both dimensions Postgres
        /// enforces: each VARCHAR(255) company column measured in characters,
        /// and the whole key's UTF-8 byte length against the btree index-row
        /// limit. Consistent with <see cref="ScarfPage"/>'s
        /// <c>TryNormalisePath</c>, which already rejects rather than
        /// truncates an over-long <c>page_path</c> for the same reason.
        /// </summary>
        private static bool IsStorableKey(
            DateOnly week, string site, string path, string companyName, string companyDomain)
        {
            if (companyName.Length > MaxCompanyFieldLength ||
                companyDomain.Length > MaxCompanyFieldLength)
            {
                Console.WriteLine(
                    $"Skipping Scarf Diagrid page row for site '{site}': " +
                    $"company_name is {companyName.Length} characters and " +
                    $"company_domain is {companyDomain.Length} characters, " +
                    $"exceeding the {MaxCompanyFieldLength}-character limit.");
                return false;
            }

            var weekText = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var keyByteLength =
                Encoding.UTF8.GetByteCount(weekText) +
                Encoding.UTF8.GetByteCount(site) +
                Encoding.UTF8.GetByteCount(path) +
                Encoding.UTF8.GetByteCount(companyName) +
                Encoding.UTF8.GetByteCount(companyDomain);

            if (keyByteLength > MaxKeyByteLength)
            {
                // Truncated to 40 characters so a long path is not dumped in
                // full into a world-readable log.
                var pathPrefix = path.Length > 40 ? path[..40] + "..." : path;
                Console.WriteLine(
                    $"Skipping Scarf Diagrid page row for site '{site}': key " +
                    $"is {keyByteLength} UTF-8 bytes, exceeding the " +
                    $"{MaxKeyByteLength}-byte budget (page_path prefix " +
                    $"'{pathPrefix}').");
                return false;
            }

            return true;
        }

        /// <summary>
        /// The per-week, per-site DELETE. Extracted so its parameter order can be
        /// asserted: it is the only statement here capable of unrecoverable data
        /// loss, and a swapped parameter array would delete the wrong rows.
        /// </summary>
        internal static (string Sql, object[] Parameters) BuildDeleteForSite(
            string weekText, string site) =>
            ($"delete from {TableName} where week_start = $1::date and site = $2",
             [weekText, site]);

        /// <summary>
        /// Delete-then-insert per ISO week, for the same reason as the
        /// existing tables: a page Scarf re-attributes to a different
        /// company must not linger, and an upsert would leave the old
        /// (page, company) pair behind as a phantom forever. The DELETE is
        /// also scoped per site so a site whose pixel has gone quiet across
        /// all three weeks — filtered out of <paramref name="sites"/> by the
        /// caller — can never have its stored rows deleted here, while a
        /// healthy site that simply had a quiet week still gets that week's
        /// DELETE, clearing any stale rows before storing nothing for it.
        /// The binding has no transaction, so a failure between a site's
        /// delete and its last insert leaves that site's week short until
        /// the next run's three-week overlap repairs it.
        /// </summary>
        private async Task StoreAsync(
            DateOnly weekStart, IReadOnlyList<PageRow> rows, string[] sites)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var collectionDate = DateTime.UtcNow;

            foreach (var site in sites)
            {
                // Scoped by site so a site whose pixel has gone quiet can
                // never delete its own history, and a healthy site still
                // stores.
                var delete = BuildDeleteForSite(weekText, site);
                await _output.InsertAsync(delete.Sql, delete.Parameters);

                var siteRows = rows.Where(row => row.Site == site).ToArray();
                if (siteRows.Length == 0)
                {
                    continue;
                }

                foreach (var chunk in siteRows.Chunk(RowsPerStatement))
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
