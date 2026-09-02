using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapr.Client;
using Dapr.Workflow;

namespace DaprStats
{
    /// <summary>
    /// Maintains the registry of every identified user seen on a signed-in RUM
    /// service, per (service, env), with a first-seen week where knowable.
    /// </summary>
    public class GetDataDogRumIdentifiedUsers
        : WorkflowActivity<DataDogRumIdentifiedInput, bool>
    {
        // The same three complete ISO weeks GetDataDogRumIdentifiedData
        // collects, so install_date always lands on a Monday that has a
        // datadog_rum_identified row for the view to seed against.
        private const int WeeksPerRun = 3;

        // DataDog's default maximum for field grouping. Current volume is ~29
        // users per week on the busier host, so a single week is nowhere near
        // it.
        private const int GroupByLimit = 1000;

        private const string AggregateUrl =
            "https://api.datadoghq.com/api/v2/rum/analytics/aggregate";

        private const string SecretStore = "secretstore";
        private const string ApiKeySecret = "DATADOGAPIKEY";
        private const string AppKeySecret = "DATADOGAPPKEY";

        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDataDogRumIdentifiedUsers(
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

            // CompleteWeeksBefore returns oldest first, which is exactly the
            // order DeriveEntries needs.
            var weeks = IsoWeek.CompleteWeeksBefore(DateTime.UtcNow, WeeksPerRun);
            var collected =
                new List<(DateOnly WeekStart,
                          IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users)>();
            var allSucceeded = true;

            foreach (var week in weeks)
            {
                try
                {
                    var (users, attributesDegraded) =
                        await FetchWeekAsync(input, week, apiKey, appKey);

                    if (attributesDegraded)
                    {
                        // The id set still came back -- this week's users are
                        // still registered, just with null attributes that a
                        // later successful run can fill in via StoreAsync's
                        // coalesce. The run is degraded, though, so it must not
                        // report full success.
                        allSucceeded = false;
                    }

                    if (users.Count >= GroupByLimit)
                    {
                        Console.WriteLine(
                            $"WARNING: week {week.WeekStart:yyyy-MM-dd} returned " +
                            $"{users.Count} users, at the group-by limit of " +
                            $"{GroupByLimit}. Users are being dropped.");
                    }

                    collected.Add((week.WeekStart, users));
                }
                catch (Exception ex)
                {
                    // A missing week costs precision on install_date for users
                    // first seen that week, and the next run refetches it
                    // anyway. It cannot corrupt a stored row, because the write
                    // never moves install_date.
                    Console.WriteLine(
                        $"Failed to collect DataDog RUM identified users for " +
                        $"{input.Service}@{input.Env} week {week.WeekStart:yyyy-MM-dd}: " +
                        ex.Message);
                    allSucceeded = false;
                }
            }

            var entries = DataDogRumIdentifiedUsers.DeriveEntries(collected);

            var withoutInstallDate = entries.Count(e => e.InstallDate is null);
            var withoutEmail = entries.Count(e => e.Email is null);

            // Counts only -- never an id, email or name.
            Console.WriteLine(
                $"DataDog RUM identified users {input.Service}@{input.Env}: " +
                $"{entries.Count} distinct users over {collected.Count} weeks " +
                $"({withoutInstallDate} with no install date, " +
                $"{withoutEmail} with no email)");

            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input, entry);
                }
            }

            return allSucceeded;
        }

        // Three requests, not one. A multi-facet group_by drops events that are
        // missing any one of the grouped facets -- confirmed empirically against
        // the live API -- so any conjunctive grouping (e.g. one request grouped
        // by [id, email, name]) would silently lose every user missing at least
        // one of those attributes, including their id. Request "ids", grouped by
        // id alone, is therefore the authoritative set: every id it returns
        // produces an Observation. "emails" and "names" are each grouped by
        // [id, <attribute>] and only supply that one attribute, left-joined onto
        // "ids" by user id -- an id absent from either simply keeps a null
        // attribute instead of being dropped. Do not "simplify" this back to one
        // request.
        //
        // The two failure boundaries below are deliberately different sizes.
        // Request "ids" failing must fail the whole week -- the outer per-week
        // try/catch in RunAsync handles that, because without an id set there is
        // nothing to register. But "emails"/"names" failing must NOT discard the
        // id set that already succeeded: an id set collected only in the oldest
        // week of the 3-week lookback would otherwise be lost permanently, since
        // that week rolls out of range before the next run. So only the two
        // attribute requests are wrapped in an inner try/catch, and a failure
        // there falls back to the ids-only observations (null email and name,
        // which a later run's coalesce can fill in) rather than losing the week.
        private async Task<(IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users,
            bool AttributesDegraded)> FetchWeekAsync(
                DataDogRumIdentifiedInput input, IsoWeek.Window week,
                string apiKey, string appKey)
        {
            var ids = await PostAsync(
                BuildGroupedBody(input, week, [DataDogRumIdentifiedUsers.UserIdFacet]),
                apiKey, appKey);

            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> emails;
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> names;
            try
            {
                emails = await PostAsync(
                    BuildGroupedBody(input, week,
                        [DataDogRumIdentifiedUsers.UserIdFacet, DataDogRumIdentifiedUsers.EmailFacet]),
                    apiKey, appKey);
                names = await PostAsync(
                    BuildGroupedBody(input, week,
                        [DataDogRumIdentifiedUsers.UserIdFacet, DataDogRumIdentifiedUsers.NameFacet]),
                    apiKey, appKey);
            }
            catch (Exception ex)
            {
                // Counts and week only -- never a user id, email or name. The
                // error message is DataDog's own error text (see PostAsync),
                // never result data.
                Console.WriteLine(
                    $"WARNING: attributes unavailable for week " +
                    $"{week.WeekStart:yyyy-MM-dd} ({ids.Count} ids collected): " +
                    ex.Message);

                return (ids, true);
            }

            var emailById = FirstNonNullByUserId(emails, u => u.Email);
            var nameById = FirstNonNullByUserId(names, u => u.Name);

            var users = ids
                .Select(u => new DataDogRumIdentifiedUsers.Observation(
                    u.UserId,
                    emailById.TryGetValue(u.UserId, out var email) ? email : null,
                    nameById.TryGetValue(u.UserId, out var name) ? name : null))
                .ToList();

            return (users, false);
        }

        private static Dictionary<string, string> FirstNonNullByUserId(
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> observations,
            Func<DataDogRumIdentifiedUsers.Observation, string?> selector)
        {
            var byId = new Dictionary<string, string>();
            foreach (var observation in observations)
            {
                var value = selector(observation);
                if (value is null)
                {
                    continue;
                }

                // If the same id appears more than once in this response, keep
                // whichever bucket DataDog returned first for it -- in practice
                // the bucket with the highest session count, since compute
                // results are ordered by the aggregation descending by default.
                // A deliberate tie-break, not an accidental one.
                byId.TryAdd(observation.UserId, value);
            }

            return byId;
        }

        private static string BuildGroupedBody(
            DataDogRumIdentifiedInput input, IsoWeek.Window week, string[] facets)
        {
            return JsonSerializer.Serialize(new
            {
                compute = new object[] { new { aggregation = "count", type = "total" } },
                group_by = facets
                    .Select(facet => new { facet, limit = GroupByLimit })
                    .ToArray(),
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    query =
                        $"@type:session @session.type:user service:{input.Service} " +
                        $"env:{input.Env} @usr.id:*"
                }
            });
        }

        private async Task<IReadOnlyList<DataDogRumIdentifiedUsers.Observation>> PostAsync(
            string body, string apiKey, string appKey)
        {
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

            return DataDogRumIdentifiedUsers.ParseUsers(payload);
        }

        private async Task StoreAsync(
            DataDogRumIdentifiedInput input, DataDogRumIdentifiedUsers.Entry entry)
        {
            const string tableName = "datadog_rum_identified_users";

            // `do update` on the attributes only. install_date and
            // collection_date are absent from the update list on purpose: a
            // later run has a strictly shorter view of history, so it must never
            // overwrite a recorded first date. The coalesce stops a week where
            // an attribute was absent from erasing a known value.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, user_id, user_email, user_name, install_date, collection_date) " +
                "values ($1, $2, $3, $4, $5, $6::date, $7) " +
                "on conflict (service, env, user_id) do update " +
                $"set user_email = coalesce(excluded.user_email, {tableName}.user_email), " +
                $"    user_name = coalesce(excluded.user_name, {tableName}.user_name)";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                entry.UserId,
                entry.Email!,
                entry.Name!,
                entry.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
