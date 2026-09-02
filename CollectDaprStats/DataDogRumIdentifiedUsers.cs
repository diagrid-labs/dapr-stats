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
        private const string UserIdFacet = "@usr.id";
        private const string EmailFacet = "@usr.email";
        private const string NameFacet = "@usr.name";

        /// <summary>One bucket of one week's grouped response.</summary>
        public sealed record Observation(string UserId, string? Email, string? Name);

        /// <summary>One row of the registry.</summary>
        public sealed record Entry(
            string UserId, string? Email, string? Name, DateOnly? InstallDate);

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
                    userId, ReadFacet(by, EmailFacet), ReadFacet(by, NameFacet)));
            }

            return users;
        }

        /// <summary>
        /// One entry per distinct id: the start of the earliest ISO week it was
        /// observed in, and the newest known email and name.
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
            var attributes = new Dictionary<string, (string? Email, string? Name)>();
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
                    attributes[user.UserId] =
                        (user.Email ?? known.Email, user.Name ?? known.Name);
                }
            }

            return firstSeen
                .Select(kv => new Entry(
                    kv.Key,
                    attributes[kv.Key].Email,
                    attributes[kv.Key].Name,
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
