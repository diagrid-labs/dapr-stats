using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parsing and first-seen derivation for the anonymous user registry.
    /// </summary>
    public static class DataDogRumUsers
    {
        private const string Facet = "@usr.anonymous_id";

        public sealed record Entry(string AnonymousId, DateOnly? InstallDate);

        /// <summary>
        /// The distinct anonymous ids in one week's grouped aggregate response.
        /// </summary>
        public static IReadOnlyList<string> ParseIds(ReadOnlySpan<byte> json)
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

            var ids = new List<string>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                // Sessions with no anonymous id group into a bucket without the
                // facet key. There is nothing to register, so skip it.
                // This is why the registry count can be below datadog_rum's
                // unique_user_count for the same week.
                if (bucket.TryGetProperty("by", out var by) &&
                    by.TryGetProperty(Facet, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var id = value.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        /// <summary>
        /// One entry per distinct id, with the start of the earliest ISO week it
        /// was observed in.
        /// </summary>
        /// <param name="weeksOldestFirst">
        /// Must be ordered oldest week first. Weeks with no ids are allowed.
        /// </param>
        /// <remarks>
        /// An id first seen in the oldest week that returned any data gets a null
        /// install date: that week is the edge of what was queried, so the id was
        /// probably active before it and its true first week is unknowable.
        /// </remarks>
        public static IReadOnlyList<Entry> DeriveEntries(
            IReadOnlyList<(DateOnly WeekStart, IReadOnlyList<string> Ids)> weeksOldestFirst)
        {
            var firstSeen = new Dictionary<string, DateOnly>();
            DateOnly? boundary = null;

            foreach (var (weekStart, ids) in weeksOldestFirst)
            {
                if (ids.Count == 0)
                {
                    // An empty week is not the boundary -- the boundary is the
                    // oldest week that actually produced ids.
                    continue;
                }

                boundary ??= weekStart;

                foreach (var id in ids)
                {
                    firstSeen.TryAdd(id, weekStart);
                }
            }

            return firstSeen
                .Select(kv => new Entry(kv.Key, kv.Value == boundary ? null : kv.Value))
                .ToList();
        }
    }
}
