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
        // DataDog's default maximum for single-dimension field grouping. Peak
        // observed volume is ~29 users per week on the busier host, so a single
        // week is nowhere near it.
        private const int GroupByLimit = 1000;

        // DataDog caps the *product* of every facet's limit in a group_by at
        // 10,000 groups total ("Cannot generate more than 10000 groups across
        // all dimensions"), not 10,000 per facet. A two-facet request grouped
        // by [id, attribute] must keep limit(id) * limit(attribute) under that
        // cap -- 1000 * 1000 blew it (confirmed against the live API: every
        // such request returned HTTP 400). 500 * 5 = 2500 is comfortably clear.
        // 500 leaves roughly an order of magnitude of headroom over the ~29
        // users per week (65 over a 30-day window) this service sees on its
        // busiest host. 5 is generous for how many distinct emails or names one
        // id could show in a single week -- normally exactly one of each --
        // whether DataDog treats that second limit as nested per parent bucket
        // or as a flat combination count, and the error message does not say
        // which, so the fix must hold under either reading.
        private const int AttributeUserGroupByLimit = 500;
        private const int AttributeGroupByLimit = 5;

        // The "orgs" request needs its own pair of limits, because it is the
        // one request whose second facet is genuinely multi-valued: one
        // production account has been seen under 8 organisations. Reusing
        // AttributeGroupByLimit (5) here would truncate that account to 5
        // under the per-parent reading of DataDog's cap -- a smaller version
        // of exactly the bug the link table exists to fix.
        //
        // 150 * 50 = 7,500, under the 10,000-groups cap and safe under either
        // reading of how that cap applies. Measured 2026-09-02 over the
        // three-week lookback: 50 distinct users on the busier host and 8
        // organisations on the busiest account, so this is ~3x and ~6x
        // headroom respectively.
        private const int OrgUserGroupByLimit = 150;
        private const int OrgGroupByLimit = 50;

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
            // order DeriveEntries needs. WeeksPerRun is shared with
            // GetDataDogRumIdentifiedData so both activities cover the same
            // three weeks and the two tables stay consistent with each other;
            // the view seeds a null install_date from the earliest week on
            // record in datadog_rum_identified for that (service, env), not
            // from any particular Monday this activity happens to touch.
            var weeks = IsoWeek.CompleteWeeksBefore(
                DateTime.UtcNow, DataDogRumIdentifiedUsers.WeeksPerRun);
            var collected =
                new List<(DateOnly WeekStart,
                          IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users)>();
            var collectedOrgs =
                new List<(DateOnly WeekStart,
                          IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Orgs)>();
            var allSucceeded = true;

            foreach (var week in weeks)
            {
                try
                {
                    var (users, orgPairs, attributesDegraded) =
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

                    // Per-request truncation warnings (one per request, each
                    // against the limit that request was given) are logged
                    // inside PostAsync, since the merged list here is always
                    // exactly as long as the "ids" request's result and cannot
                    // by itself reveal whether "emails" or "names" was
                    // truncated at its own, smaller limit.
                    collected.Add((week.WeekStart, users));
                    collectedOrgs.Add((week.WeekStart, orgPairs));
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
            var orgLinks = DataDogRumIdentifiedUsers.DeriveOrgLinks(collectedOrgs);

            var withoutInstallDate = entries.Count(e => e.InstallDate is null);
            var withoutEmail = entries.Count(e => e.Email is null);
            var withoutOrg = entries.Count(e => e.Organization is null);

            // Counts only -- never an id, email or name.
            Console.WriteLine(
                $"DataDog RUM identified users {input.Service}@{input.Env}: " +
                $"{entries.Count} distinct users over {collected.Count} weeks " +
                $"({withoutInstallDate} with no install date, " +
                $"{withoutEmail} with no email, {withoutOrg} with no org)");

            var distinctOrgs = orgLinks
                .Select(l => l.Organization)
                .Distinct()
                .Count();
            var pairsWithoutFirstSeen = orgLinks.Count(l => l.FirstSeenWeek is null);

            // Counts only -- never an id or an organisation identifier. This is
            // the number to reconcile against DataDog: 60 pairs / 23 orgs for
            // conductor.r1.diagrid.io and 4 / 3 for
            // dapr-ops-dashboard.diagrid.io over the three-week lookback as of
            // 2026-09-02.
            Console.WriteLine(
                $"DataDog RUM identified user-orgs {input.Service}@{input.Env}: " +
                $"{orgLinks.Count} pairs over {distinctOrgs} distinct organisations " +
                $"({pairsWithoutFirstSeen} with no first-seen week)");

            if (!input.SkipStorage)
            {
                foreach (var entry in entries)
                {
                    await StoreAsync(input, entry);
                }

                foreach (var link in orgLinks)
                {
                    await StoreOrgAsync(input, link);
                }
            }

            return allSucceeded;
        }

        // Four requests, not one. A multi-facet group_by drops events that are
        // missing any one of the grouped facets -- confirmed empirically against
        // the live API -- so any conjunctive grouping (e.g. one request grouped
        // by [id, email, name, organization]) would silently lose every user
        // missing at least one of those attributes, including their id. Request
        // "ids", grouped by id alone, is therefore the authoritative set: every
        // id it returns produces an Observation. "emails", "names" and "orgs"
        // are each grouped by [id, <attribute>] and only supply that one
        // attribute, left-joined onto "ids" by user id -- an id absent from any
        // of them simply keeps a null attribute instead of being dropped. Do not
        // "simplify" this back to one request.
        //
        // The two failure boundaries below are deliberately different sizes.
        // Request "ids" failing must fail the whole week -- the outer per-week
        // try/catch in RunAsync handles that, because without an id set there is
        // nothing to register. But "emails"/"names" failing must NOT discard the
        // id set that already succeeded: an id set collected only in the oldest
        // week of the 3-week lookback would otherwise be lost permanently, since
        // that week rolls out of range before the next run. So only the three
        // attribute requests are wrapped in an inner try/catch, and a failure
        // there falls back to the ids-only observations (null attributes, which
        // a later run's coalesce can fill in) rather than losing the week.
        private async Task<(IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Users,
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> OrgPairs,
            bool AttributesDegraded)> FetchWeekAsync(
                DataDogRumIdentifiedInput input, IsoWeek.Window week,
                string apiKey, string appKey)
        {
            var ids = await PostAsync(
                BuildGroupedBody(input, week,
                    [(DataDogRumIdentifiedUsers.UserIdFacet, GroupByLimit)]),
                "ids", GroupByLimit, week, apiKey, appKey);

            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> emails;
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> names;
            IReadOnlyList<DataDogRumIdentifiedUsers.Observation> orgs;
            try
            {
                emails = await PostAsync(
                    BuildGroupedBody(input, week,
                        [(DataDogRumIdentifiedUsers.UserIdFacet, AttributeUserGroupByLimit),
                         (DataDogRumIdentifiedUsers.EmailFacet, AttributeGroupByLimit)]),
                    "emails", AttributeUserGroupByLimit, week, apiKey, appKey);
                names = await PostAsync(
                    BuildGroupedBody(input, week,
                        [(DataDogRumIdentifiedUsers.UserIdFacet, AttributeUserGroupByLimit),
                         (DataDogRumIdentifiedUsers.NameFacet, AttributeGroupByLimit)]),
                    "names", AttributeUserGroupByLimit, week, apiKey, appKey);
                orgs = await PostAsync(
                    BuildGroupedBody(input, week,
                        [(DataDogRumIdentifiedUsers.UserIdFacet, OrgUserGroupByLimit),
                         (DataDogRumIdentifiedUsers.OrganizationFacet, OrgGroupByLimit)]),
                    "orgs", OrgUserGroupByLimit, week, apiKey, appKey);
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

                return (ids, [], true);
            }

            var emailById = FirstNonNullByUserId(emails, u => u.Email);
            var nameById = FirstNonNullByUserId(names, u => u.Name);
            var orgById = FirstNonNullByUserId(orgs, u => u.Organization);

            var users = ids
                .Select(u => new DataDogRumIdentifiedUsers.Observation(
                    u.UserId,
                    emailById.TryGetValue(u.UserId, out var email) ? email : null,
                    nameById.TryGetValue(u.UserId, out var name) ? name : null,
                    orgById.TryGetValue(u.UserId, out var org) ? org : null))
                .ToList();

            return (users, orgs, false);
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

        // Each facet carries its own limit: request "ids" needs only
        // (id, GroupByLimit); requests "emails"/"names" need the smaller
        // (id, AttributeUserGroupByLimit) and (attribute, AttributeGroupByLimit)
        // pair so their product stays under DataDog's 10,000-groups cap. A
        // single limit shared across every facet is exactly the bug this method
        // exists to prevent -- do not collapse this back to `string[] facets`.
        private static string BuildGroupedBody(
            DataDogRumIdentifiedInput input, IsoWeek.Window week,
            (string Facet, int Limit)[] facets)
        {
            return JsonSerializer.Serialize(new
            {
                compute = new object[] { new { aggregation = "count", type = "total" } },
                group_by = facets
                    .Select(f => new { facet = f.Facet, limit = f.Limit })
                    .ToArray(),
                filter = new
                {
                    from = Iso8601(week.From),
                    to = Iso8601(week.To),
                    // Shared with GetDataDogRumIdentifiedData so the aggregate
                    // and the registry always describe the same population.
                    query = DataDogRumIdentifiedUsers.SearchQuery(input.Service, input.Env)
                }
            });
        }

        // `limit` is the limit that most directly bounds this request's
        // coverage -- for "ids" that is the single id-facet limit; for
        // "emails"/"names" it is the id-facet limit (AttributeUserGroupByLimit),
        // since that is what determines whether every id got a chance at an
        // attribute. A bucket count at or above it means the response may be
        // truncated, so callers relying on it for full coverage should know.
        private async Task<IReadOnlyList<DataDogRumIdentifiedUsers.Observation>> PostAsync(
            string body, string requestName, int limit, IsoWeek.Window week,
            string apiKey, string appKey)
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

            var users = DataDogRumIdentifiedUsers.ParseUsers(payload);

            if (users.Count >= limit)
            {
                // Counts, week and request name only -- never a user id, email
                // or name.
                Console.WriteLine(
                    $"WARNING: \"{requestName}\" request for week " +
                    $"{week.WeekStart:yyyy-MM-dd} returned {users.Count} buckets, " +
                    $"at the group-by limit of {limit}. Results may be truncated.");
            }

            return users;
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
                "(service, env, user_id, user_email, user_name, user_organization, " +
                " install_date, collection_date) " +
                "values ($1, $2, $3, $4, $5, $6, $7::date, $8) " +
                "on conflict (service, env, user_id) do update " +
                $"set user_email = coalesce(excluded.user_email, {tableName}.user_email), " +
                $"    user_name = coalesce(excluded.user_name, {tableName}.user_name), " +
                $"    user_organization = " +
                $"        coalesce(excluded.user_organization, {tableName}.user_organization)";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                entry.UserId,
                entry.Email!,
                entry.Name!,
                entry.Organization!,
                entry.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private async Task StoreOrgAsync(
            DataDogRumIdentifiedInput input, DataDogRumIdentifiedUsers.OrgLink link)
        {
            const string tableName = "datadog_rum_identified_user_orgs";

            // `do nothing`, not `do update`. A later run has a strictly
            // shorter view of history, so it must never move a recorded first
            // date -- the same reasoning that keeps install_date out of the
            // registry's update list. In particular a pair recorded with a null
            // first_seen_week at the boundary must not be "corrected" by a
            // later run, whose boundary is later and therefore worse.
            var sqlText =
                $"insert into {tableName} " +
                "(service, env, user_id, organization, first_seen_week, " +
                " collection_date) " +
                "values ($1, $2, $3, $4, $5::date, $6) " +
                "on conflict (service, env, user_id, organization) do nothing";

            var sqlParameters = new object[]
            {
                input.Service,
                input.Env,
                link.UserId,
                link.Organization,
                link.FirstSeenWeek?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)!,
                DateTime.UtcNow
            };

            await _output.InsertAsync(sqlText, sqlParameters);
        }

        private static string Iso8601(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
