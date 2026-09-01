using System.Globalization;
using System.Text;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetScarfCompanyViews : WorkflowActivity<ScarfInput, bool>
    {
        // The same three complete ISO weeks GetScarfBuildingBlockViews writes,
        // so both tables always cover the same range.
        private const int WeeksPerRun = 3;

        private const string ExportUrl =
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export";

        private const string SecretStore = "secretstore";
        private const string ApiTokenSecret = "SCARFAPITOKEN";

        private const string TableName = "scarf_company_views";

        private const int RowsPerStatement = 500;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null];

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetScarfCompanyViews(
            IHttpClientFactory httpClientFactory,
            PostgresOutput output,
            DaprClient daprClient)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
            _daprClient = daprClient;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            ScarfInput input)
        {
            var secrets = await _daprClient.GetSecretAsync(SecretStore, ApiTokenSecret);
            var token = secrets[ApiTokenSecret];

            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            var rows = await FetchAsync(input.PixelIds, from, to, token);
            var byWeek = Aggregate(rows);

            var allSucceeded = true;

            foreach (var week in weeks)
            {
                // Not `: []` — a collection expression in a conditional has no
                // natural type for `var` to infer.
                var weekRows = byWeek.TryGetValue(week.WeekStart, out var found)
                    ? found
                    : new List<CompanyRow>();

                Console.WriteLine(
                    $"Scarf companies week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} companies, " +
                    $"{weekRows.Sum(r => r.Views)} views");

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
                        $"Failed to store Scarf company views for week " +
                        $"{week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            string[] pixelIds, DateOnly from, DateOnly to, string token)
        {
            var url = new StringBuilder(ExportUrl);
            url.Append("?start_date=").Append(Iso(from));
            url.Append("&end_date=").Append(Iso(to));

            foreach (var pixelId in pixelIds)
            {
                url.Append("&tracking_pixel_id=").Append(Uri.EscapeDataString(pixelId));
            }

            url.Append("&rollup=weekly");
            url.Append("&breakdown=by-company");

            // group_by_artifact=false is what merges the two pixels
            // server-side. Without it Scarf returns one row per pixel and
            // unique_origins cannot be summed without double-counting anyone
            // who visited both dapr.io and docs.dapr.io.
            url.Append("&group_by_artifact=false");
            url.Append("&format=json");

            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            request.Headers.Add("Authorization", $"Bearer {token}");

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Scarf returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return ScarfAggregationResponse.Parse(payload);
        }

        /// <summary>
        /// Groups rows by week, guarding the unique constraint.
        /// </summary>
        /// <remarks>
        /// With group_by_artifact=false Scarf already returns one row per
        /// (week, company), so this is defensive. Views are summed; unique
        /// visitors take the maximum rather than the sum, because summing
        /// distinct-origin counts is never correct and Max is the identity
        /// when there is only one row, as expected.
        /// </remarks>
        private static Dictionary<DateOnly, List<CompanyRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                var key = (row.WeekStart, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               Math.Max(running.UniqueVisitors, row.UniqueOrigins));
            }

            var byWeek = new Dictionary<DateOnly, List<CompanyRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new CompanyRow(
                    key.Name, key.Domain, value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<CompanyRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert, for the same reason as the building-block
            // table: a company Scarf re-attributes would otherwise linger.
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
                    "(week_start, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.CompanyName);
                    parameters.Add(row.CompanyDomain);
                    parameters.Add(row.Views);
                    parameters.Add(row.UniqueVisitors);
                    parameters.Add(collectionDate);
                }

                await _output.InsertAsync(sqlText, parameters.ToArray());
            }
        }

        private static string Iso(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private sealed record CompanyRow(
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }
}
