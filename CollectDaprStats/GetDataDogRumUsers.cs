using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetDataDogRumUsers : WorkflowActivity<DataDogRumInput, bool>
    {
        // The same three complete ISO weeks GetDataDogRumData collects, so
        // install_date always lands on a Monday that has a datadog_rum row.
        private const int WeeksPerRun = 3;

        // DataDog's default maximum for field grouping. Current volume is ~80
        // distinct ids per 30 days, so a single week is nowhere near it.
        private const int GroupByLimit = 1000;

        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumUsers(
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

            // CompleteWeeksBefore returns oldest first, which is exactly the
            // order DeriveEntries needs.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var collected = new List<(DateOnly WeekStart, IReadOnlyList<string> Ids)>();
            var allSucceeded = true;

            foreach (var week in weeks)
            {
                try
                {
                    var ids = await FetchWeekAsync(input.Service, week, apiKey, appKey);

                    if (ids.Count >= GroupByLimit)
                    {
                        Console.WriteLine(
                            $"WARNING: week {week.WeekStart:yyyy-MM-dd} returned " +
                            $"{ids.Count} ids, at the group-by limit of {GroupByLimit}. " +
                            "Ids are being dropped.");
                    }

                    collected.Add((week.WeekStart, ids));
                }
                catch (Exception ex)
                {
                    // A missing week costs precision on install_date for ids first
                    // seen that week, and the next run refetches it anyway. It
                    // cannot corrupt a stored row, because the insert is
                    // `do nothing`.
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM users for {input.Service} " +
                        $"week {week.WeekStart:yyyy-MM-dd}: {ex.Message}");
                    allSucceeded = false;
                }
            }

            var entries = DataDogRumUsers.DeriveEntries(collected);

            var withoutInstallDate = entries.Count(e => e.InstallDate is null);
            Console.WriteLine(
                $"DataDog RUM users {input.Service}: {entries.Count} distinct anonymous " +
                $"ids over {collected.Count} weeks ({withoutInstallDate} with no " +
                "install date)");

            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input.Service, entry);
                }
            }

            return allSucceeded;
        }

        private async Task<IReadOnlyList<string>> FetchWeekAsync(
            string service, IsoWeek.Window week, string apiKey, string appKey)
        {
            var body = JsonSerializer.Serialize(new
            {
                compute = new object[] { new { aggregation = "count", type = "total" } },
                group_by = new object[]
                {
                    new { facet = "@usr.anonymous_id", limit = GroupByLimit }
                },
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query = $"@type:session @session.type:user service:{service} env:prod"
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

            return DataDogRumUsers.ParseIds(payload);
        }

        private async Task StoreAsync(string service, DataDogRumUsers.Entry entry)
        {
            const string tableName = "datadog_rum_users";

            // `do nothing`, not `do update`: a later run has a strictly shorter
            // view of history, so it must never overwrite a recorded first date.
            var sqlText =
                $"insert into {tableName} " +
                "(service, user_anonymous_id, install_date, collection_date) " +
                "values ($1, $2, $3::date, $4) " +
                "on conflict (service, user_anonymous_id) do nothing";

            var sqlParameters = new object[]
            {
                service,
                entry.AnonymousId,
                entry.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
