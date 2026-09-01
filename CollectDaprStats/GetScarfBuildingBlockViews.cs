using System.Globalization;
using System.Text;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetScarfBuildingBlockViews : WorkflowActivity<ScarfInput, bool>
    {
        // Complete ISO weeks fetched and rewritten per run. Unlike DataDog RUM
        // this is not a retention limit — Scarf keeps years. Scarf's company
        // attribution is retroactive, so a visitor identified days later
        // changes an earlier week, and the overlap lets that correction land.
        private const int WeeksPerRun = 3;

        // `Dapr` is capitalised deliberately. /v2/* accepts `dapr`, but /v3/*
        // returns 404 {"detail":"Organization not found"} for anything but
        // `Dapr`. Confirmed 2026-09-01.
        private const string ExportUrl =
            "https://api.scarf.sh/v3/insights/Dapr/aggregations/export";

        private const string SecretStore = "secretstore";
        private const string ApiTokenSecret = "SCARF_DAPR_API_TOKEN";

        private const string TableName = "scarf_building_block_views";

        // 500 x 7 = 3,500 parameters per statement, far inside Postgres' 65,535
        // limit. A busy week is ~830 rows, so two statements.
        private const int RowsPerStatement = 500;

        private static readonly string?[] ColumnCasts =
            ["date", null, null, null, null, null, null];

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetScarfBuildingBlockViews(
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

            // One request covers all three weeks: rollup=weekly returns one
            // bucket per week. end_date is exclusive, verified 2026-09-01.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var from = DateOnly.FromDateTime(weeks[0].From);
            var to = DateOnly.FromDateTime(weeks[^1].To);

            IReadOnlyList<ScarfAggregationResponse.Row> rows;
            try
            {
                rows = await FetchAsync(input.PixelIds, from, to, token);
            }
            catch (Exception ex)
            {
                // Not rethrown: this activity runs under Task.WhenAll
                // alongside GetScarfCompanyViews in CollectorWorkflow, and an
                // unhandled exception here would fail the whole workflow and
                // skip the unrelated GitHub collection below it. The bool
                // return exists so a Scarf outage is reported without taking
                // anything else down.
                Console.WriteLine(
                    $"Failed to fetch Scarf building block data: {ex.Message}");
                return false;
            }

            var byWeek = Aggregate(rows);

            // Zero rows across every week, from here, is indistinguishable
            // between a genuinely quiet three weeks and two failure modes
            // that both look healthy: Scarf answering an unrecognised
            // tracking_pixel_id with HTTP 200 and {"data":[]}, or a docs-site
            // restructure that stops /building-blocks/ appearing in any
            // referer so Aggregate drops every row. Either would otherwise
            // delete three real weeks of data below and write nothing back,
            // and the three-week window means the oldest of them is gone for
            // good before the next run could repair it. Bail out before the
            // first DELETE rather than risk that.
            if (byWeek.Values.Sum(list => list.Count) == 0)
            {
                Console.WriteLine(
                    "WARNING: Scarf building blocks returned zero rows across " +
                    $"all {WeeksPerRun} weeks. Skipping storage entirely " +
                    "(no deletes performed). Likely causes: a wrong or " +
                    "revoked tracking_pixel_id, or a docs restructure that " +
                    "moved /building-blocks/ out of the referer paths.");
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
                    : new List<BuildingBlockRow>();

                Console.WriteLine(
                    $"Scarf building blocks week {week.WeekStart:yyyy-MM-dd}: " +
                    $"{weekRows.Count} rows, " +
                    $"{weekRows.Select(r => r.BuildingBlock).Distinct().Count()} building blocks, " +
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
                        $"Failed to store Scarf building block views for week " +
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

            // The comma is left unescaped: this is the exact form verified
            // against the live API on 2026-09-01.
            url.Append("&breakdown_set=by-referer,by-company");
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
        /// Maps referers onto building blocks and re-sums the result, keyed by
        /// week.
        /// </summary>
        /// <remarks>
        /// Normalisation is many-to-one: query-string variants, versioned
        /// hosts, localised paths and every page beneath a building block all
        /// collapse onto one key. Without re-summing here the insert would
        /// carry duplicate keys and violate the unique constraint.
        /// </remarks>
        private static Dictionary<DateOnly, List<BuildingBlockRow>> Aggregate(
            IReadOnlyList<ScarfAggregationResponse.Row> rows)
        {
            var totals = new Dictionary<
                (DateOnly Week, string Block, string Name, string Domain),
                (long Views, long UniqueVisitors)>();

            foreach (var row in rows)
            {
                if (!ScarfReferer.TryGetBuildingBlock(row.Referer, out var block))
                {
                    continue;
                }

                var key = (row.WeekStart, block, row.CompanyName, row.CompanyDomain);
                totals.TryGetValue(key, out var running);
                totals[key] = (running.Views + row.Total,
                               running.UniqueVisitors + row.UniqueOrigins);
            }

            var byWeek = new Dictionary<DateOnly, List<BuildingBlockRow>>();

            foreach (var (key, value) in totals)
            {
                if (!byWeek.TryGetValue(key.Week, out var list))
                {
                    list = [];
                    byWeek[key.Week] = list;
                }

                list.Add(new BuildingBlockRow(
                    key.Block, key.Name, key.Domain, value.Views, value.UniqueVisitors));
            }

            return byWeek;
        }

        private async Task StoreAsync(DateOnly weekStart, IReadOnlyList<BuildingBlockRow> rows)
        {
            var weekText = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Delete-then-insert rather than upsert: Scarf re-attributing a
            // visitor can remove a (building block, company) pair from a week,
            // and an upsert would leave the old pair behind as a phantom for
            // ever. The binding has no transaction, so a failure between the
            // delete and the last insert leaves the week short until the next
            // run's three-week overlap repairs it.
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
                    "(week_start, building_block, company_name, company_domain, " +
                    " views, unique_visitors, collection_date) " +
                    $"values {SqlValuesBuilder.Build(chunk.Length, ColumnCasts)}";

                var parameters = new List<object>(chunk.Length * ColumnCasts.Length);

                foreach (var row in chunk)
                {
                    parameters.Add(weekText);
                    parameters.Add(row.BuildingBlock);
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

        private sealed record BuildingBlockRow(
            string BuildingBlock,
            string CompanyName,
            string CompanyDomain,
            long Views,
            long UniqueVisitors);
    }

    public record ScarfInput(string[] PixelIds, bool SkipStorage);
}
