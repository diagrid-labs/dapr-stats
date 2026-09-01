using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parses a RUM analytics aggregate response. Computes are keyed
    /// positionally by request order, so `c0` is the first compute in the
    /// request body and `c1` the second.
    /// </summary>
    public static class DataDogRumResponse
    {
        private const string SessionCountKey = "c0";
        private const string UniqueUserCountKey = "c1";

        public static (long SessionCount, long UniqueUserCount) Parse(ReadOnlySpan<byte> json)
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
                // No sessions matched the query. That is a real zero week.
                return (0, 0);
            }

            if (!buckets[0].TryGetProperty("computes", out var computes))
            {
                throw new FormatException("DataDog response bucket has no computes object.");
            }

            return (ReadCount(computes, SessionCountKey),
                    ReadCount(computes, UniqueUserCountKey));
        }

        private static long ReadCount(JsonElement computes, string key)
        {
            if (!computes.TryGetProperty(key, out var value))
            {
                throw new FormatException($"DataDog response has no compute '{key}'.");
            }

            return value.ValueKind switch
            {
                // Counts arrive as JSON numbers that are not always integral.
                JsonValueKind.Number => (long)value.GetDouble(),
                JsonValueKind.Null => 0L,
                _ => throw new FormatException(
                    $"DataDog compute '{key}' is {value.ValueKind}, expected a number."),
            };
        }
    }
}
