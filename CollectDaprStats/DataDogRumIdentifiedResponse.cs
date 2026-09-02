using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parses the weekly aggregate response for a signed-in RUM service:
    /// a session count, a distinct-user cardinality and a distinct-organisation
    /// cardinality.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="DataDogRumResponse"/> rather than an extension
    /// of it. That class reads exactly two computes and backs the
    /// dev-dashboard collector, which runs weekly in production; leaving it
    /// alone means a third compute here cannot affect it.
    /// <para>
    /// Computes are keyed positionally by request order, so <c>c0</c> is the
    /// first compute in the request body, <c>c1</c> the second and <c>c2</c>
    /// the third. Reordering the request body silently reassigns these.
    /// </para>
    /// </remarks>
    public static class DataDogRumIdentifiedResponse
    {
        private const string SessionCountKey = "c0";
        private const string UniqueUserCountKey = "c1";
        private const string OrgCountKey = "c2";

        public static (long SessionCount, long UniqueUserCount, long OrgCount) Parse(
            ReadOnlySpan<byte> json)
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

            if (buckets.GetArrayLength() == 0)
            {
                // No sessions matched the query. That is a real zero week --
                // dapr-ops-dashboard has weeks like this.
                return (0, 0, 0);
            }

            if (!buckets[0].TryGetProperty("computes", out var computes))
            {
                throw new FormatException("DataDog response bucket has no computes object.");
            }

            return (ReadCount(computes, SessionCountKey),
                    ReadCount(computes, UniqueUserCountKey),
                    ReadCount(computes, OrgCountKey));
        }

        private static long ReadCount(JsonElement computes, string key)
        {
            // Deliberately not defaulting a missing compute to zero: a compute
            // silently absent would store a zero for a week that had a real
            // value, which is indistinguishable from a genuinely quiet week.
            if (!computes.TryGetProperty(key, out var value))
            {
                throw new FormatException($"DataDog response has no compute '{key}'.");
            }

            return value.ValueKind switch
            {
                // Counts arrive as JSON numbers that are not always integral:
                // cardinality is an estimate.
                JsonValueKind.Number => (long)value.GetDouble(),
                JsonValueKind.Null => 0L,
                _ => throw new FormatException(
                    $"DataDog compute '{key}' is {value.ValueKind}, expected a number."),
            };
        }
    }
}
