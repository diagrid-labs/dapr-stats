using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parsing and derivation for the identified-user registry.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="DataDogRumUsers"/> rather than a generalisation
    /// of it. That class backs the dev-dashboard collector, which runs weekly
    /// in production and has no identity attributes at all; keeping the two
    /// apart means changes here cannot affect it.
    /// </remarks>
    public static class DataDogRumIdentifiedUsers
    {
        public const string UserIdFacet = "@usr.id";
        public const string EmailFacet = "@usr.email";
        public const string NameFacet = "@usr.name";
        public const string OrganizationFacet = "@usr.organization";

        // Complete ISO weeks fetched per run, shared by every activity that
        // collects identified-user data (the aggregate and the registry) so
        // both always cover the same weeks. Three is the largest lookback
        // that is always inside DataDog's 30-day RUM retention: at most 6
        // days of the current partial week plus 21 days of complete weeks is
        // 27 days. Four weeks reaches 34 days, and a week only partly inside
        // the retention window returns a partial count that looks like a
        // real low week.
        public const int WeeksPerRun = 3;

        /// <summary>
        /// The DataDog RUM search query for authenticated sessions on a
        /// signed-in service. Shared by every activity that collects
        /// identified-user data, so the aggregate
        /// (<c>datadog_rum_identified</c>) and the registry
        /// (<c>datadog_rum_identified_users</c>) always describe the same
        /// population -- a change here that adds a term, drops
        /// <c>@session.type:user</c>, or changes the <c>env:</c> match cannot
        /// silently apply to only one side.
        /// </summary>
        /// <remarks>
        /// <c>@usr.id:*</c> is what makes this authenticated sessions only --
        /// 78% of conductor-ui sessions have no identified user and are
        /// excluded. <c>@session.type:user</c> excludes Synthetics traffic.
        /// <paramref name="env"/> is a hostname on this service, not `prod`.
        /// </remarks>
        public static string SearchQuery(string service, string env) =>
            $"@type:session @session.type:user service:{service} env:{env} @usr.id:*";

        /// <summary>One bucket of one week's grouped response.</summary>
        public sealed record Observation(
            string UserId, string? Email, string? Name, string? Organization);

        /// <summary>One row of the registry.</summary>
        public sealed record Entry(
            string UserId, string? Email, string? Name, string? Organization,
            DateOnly? InstallDate);

        /// <summary>
        /// The users in one week's grouped aggregate response. Buckets with no
        /// id are skipped: there is nothing to register.
        /// </summary>
        public static IReadOnlyList<Observation> ParseUsers(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                throw new FormatException("DataDog returned an empty response body.");
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("DataDog response is not valid JSON.", ex);
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("DataDog response has no data.buckets array.");
            }

            var users = new List<Observation>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("by", out var by))
                {
                    continue;
                }

                var userId = ReadFacet(by, UserIdFacet);
                if (userId is null)
                {
                    continue;
                }

                users.Add(new Observation(
                    userId,
                    ReadFacet(by, EmailFacet),
                    ReadFacet(by, NameFacet),
                    ReadFacet(by, OrganizationFacet)));
            }

            return users;
        }

        /// <summary>
        /// One entry per distinct id: the start of the earliest ISO week it was
        /// observed in, and the newest known email, name and organisation.
        /// </summary>
        /// <param name="weeksOldestFirst">
        /// Must be ordered oldest week first. Weeks with no users are allowed.
        /// </param>
        /// <remarks>
        /// An id first seen in the oldest week that returned any data gets a
        /// null install date: that week is the edge of what was queried, so the
        /// id was probably active before it and its true first week is
        /// unknowable. The view seeds those users into the earliest week on
        /// record.
        /// </remarks>
        public static IReadOnlyList<Entry> DeriveEntries(
            IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<Observation> Users)> weeksOldestFirst)
        {
            var firstSeen = new Dictionary<string, DateOnly>();
            var attributes =
                new Dictionary<string, (string? Email, string? Name, string? Organization)>();
            DateOnly? boundary = null;

            foreach (var (weekStart, users) in weeksOldestFirst)
            {
                if (users.Count == 0)
                {
                    // An empty week is not the boundary -- the boundary is the
                    // oldest week that actually produced users.
                    continue;
                }

                boundary ??= weekStart;

                foreach (var user in users)
                {
                    firstSeen.TryAdd(user.UserId, weekStart);

                    // Oldest-first iteration plus null-coalescing means the
                    // newest non-null value wins, and a week where an attribute
                    // was absent cannot erase one that is known.
                    attributes.TryGetValue(user.UserId, out var known);
                    attributes[user.UserId] = (
                        user.Email ?? known.Email,
                        user.Name ?? known.Name,
                        user.Organization ?? known.Organization);
                }
            }

            return firstSeen
                .Select(kv => new Entry(
                    kv.Key,
                    attributes[kv.Key].Email,
                    attributes[kv.Key].Name,
                    attributes[kv.Key].Organization,
                    kv.Value == boundary ? null : kv.Value))
                .ToList();
        }

        private static string? ReadFacet(JsonElement by, string facet)
        {
            if (!by.TryGetProperty(facet, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
