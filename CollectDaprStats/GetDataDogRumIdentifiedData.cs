using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Weekly authenticated-session and unique-user counts for a signed-in RUM
    /// service, per (service, env).
    /// </summary>
    public class GetDataDogRumIdentifiedData
        : WorkflowActivity<DataDogRumIdentifiedInput, bool>
    {
        // Complete ISO weeks fetched per run. Three is the largest lookback that
        // is always inside DataDog's 30-day RUM retention: at most 6 days of the
        // current partial week plus 21 days of complete weeks is 27 days. Four
        // weeks reaches 34 days, and a week only partly inside the retention
        // window returns a partial count that looks like a real low week.
        private const int WeeksPerRun = 3;

        // US1.
        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumIdentifiedData(
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
            DataDogRumIdentifiedInput input)
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
                        await FetchWeekAsync(input, week, apiKey, appKey);

                    // Counts only. Never log an id, email or name: this
                    // repository is public and its Actions logs are readable by
                    // anyone.
                    Console.WriteLine(
                        $"DataDog RUM identified {input.Service}@{input.Env} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {sessionCount} sessions, " +
                        $"{uniqueUserCount} unique users");

                    if (!input.SkipStorage)
                    {
                        await StoreAsync(input, week.WeekStart, sessionCount, uniqueUserCount);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM identified data for " +
                        $"{input.Service}@{input.Env} week {week.WeekStart:yyyy-MM-dd}: " +
                        ex.Message);
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        private async Task<(long SessionCount, long UniqueUserCount)> FetchWeekAsync(
            DataDogRumIdentifiedInput input, IsoWeek.Window week,
            string apiKey, string appKey)
        {
            // @usr.id:* is what makes this authenticated sessions only -- 78% of
            // conductor-ui sessions have no identified user and are excluded.
            // @session.type:user excludes Synthetics traffic. env is a hostname
            // on this service, not `prod`.
            var searchQuery =
                $"@type:session @session.type:user service:{input.Service} " +
                $"env:{input.Env} @usr.id:*";

            var body = JsonSerializer.Serialize(new
            {
                compute = new object[]
                {
                    new { aggregation = "count", type = "total" },
                    new
                    {
                        aggregation = "cardinality",
                        type = "total",
                        metric = "@usr.id"
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
                // The error body carries DataDog's message, not result data, so
                // it cannot contain a user attribute.
                throw new HttpRequestException(
                    $"DataDog returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            // Same response shape as the dev-dashboard collector: c0 a count,
            // c1 a cardinality. The parser is reused as-is.
            return DataDogRumResponse.Parse(payload);
        }

        private async Task StoreAsync(
            DataDogRumIdentifiedInput input, DateOnly weekStart,
            long sessionCount, long uniqueUserCount)
        {
            const string tableName = "datadog_rum_identified";

            // The unique constraint is what makes the overlapping lookback safe:
            // a week re-collected inside retention is complete both times, so
            // overwriting corrects instead of duplicating.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, week_start, session_count, unique_user_count, collection_date) " +
                "values ($1, $2, $3::date, $4, $5, $6) " +
                "on conflict (service, env, week_start) do update " +
                "set session_count = excluded.session_count, " +
                "    unique_user_count = excluded.unique_user_count, " +
                "    collection_date = excluded.collection_date";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
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

    public record DataDogRumIdentifiedInput(string Service, string Env, bool SkipStorage);
}
