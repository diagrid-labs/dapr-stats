using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetDataDogRumData : WorkflowActivity<DataDogRumInput, bool>
    {
        // Complete ISO weeks fetched per run. Three is the largest lookback that
        // is always inside DataDog's 30-day RUM retention: at most 6 days of the
        // current partial week plus 21 days of complete weeks is 27 days. Four
        // weeks reaches 34 days, and a week only partly inside the retention
        // window returns a partial count that looks like a real low week.
        private const int WeeksPerRun = 3;

        // US1. Confirmed 2026-08-26.
        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumData(
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
            DataDogRumInput input)
        {
            var apiKeySecrets = await _daprClient.GetSecretAsync(SecretStore, ApiKeySecret);
            var appKeySecrets = await _daprClient.GetSecretAsync(SecretStore, AppKeySecret);
            var apiKey = apiKeySecrets[ApiKeySecret];
            var appKey = appKeySecrets[AppKeySecret];

            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var allSucceeded = true;

            // Each week is independent: one failure must not discard the others.
            foreach (var week in weeks)
            {
                try
                {
                    var (sessionCount, uniqueUserCount) =
                        await FetchWeekAsync(input.Service, week, apiKey, appKey);

                    Console.WriteLine(
                        $"DataDog RUM {input.Service} week {week.WeekStart:yyyy-MM-dd}: " +
                        $"{sessionCount} sessions, {uniqueUserCount} unique users");

                    if (!input.SkipStorage)
                    {
                        await StoreAsync(
                            input.Service, week.WeekStart, sessionCount, uniqueUserCount);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM data for {input.Service} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<(long SessionCount, long UniqueUserCount)> FetchWeekAsync(
            string service, IsoWeek.Window week, string apiKey, string appKey)
        {
            // @session.type:user excludes Synthetics traffic and env:prod excludes
            // a staging deployment. Neither exists today; both stop a future one
            // from silently inflating the counts.
            var searchQuery =
                $"@type:session @session.type:user service:{service} env:prod";

            var body = JsonSerializer.Serialize(new
            {
                compute = new object[]
                {
                    new { aggregation = "count", type = "total" },
                    new
                    {
                        aggregation = "cardinality",
                        type = "total",
                        metric = "@usr.anonymous_id"
                    }
                },
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query = searchQuery
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, AggregateUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("DD-API-KEY", apiKey);
            request.Headers.Add("DD-APPLICATION-KEY", appKey);

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"DataDog returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return DataDogRumResponse.Parse(payload);
        }

        private async Task StoreAsync(
            string service, DateOnly weekStart, long sessionCount, long uniqueUserCount)
        {
            const string tableName = "datadog_rum";

            // The unique constraint is what makes the overlapping lookback safe:
            // re-collecting a week overwrites it instead of duplicating it.
            var sqlText =
                $"insert into {tableName} " +
                "(service, week_start, session_count, unique_user_count, collection_date) " +
                "values ($1, $2::date, $3, $4, $5) " +
                "on conflict (service, week_start) do update " +
                "set session_count = excluded.session_count, " +
                "    unique_user_count = excluded.unique_user_count, " +
                "    collection_date = excluded.collection_date";

            var sqlParameters = new object[]
            {
                service,
                weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                sessionCount,
                uniqueUserCount,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public record DataDogRumInput(string Service, bool SkipStorage);
}
