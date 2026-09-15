using System.Globalization;
using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parses the `{"data":[...]}` envelope returned by
    /// GET /v3/insights/{owner}/aggregations/export?format=json.
    /// </summary>
    /// <remarks>
    /// Each row carries around thirty fields, almost all null for any given
    /// breakdown. Only the six read here are used.
    /// </remarks>
    public static class ScarfAggregationResponse
    {
        public sealed record Row(
            DateOnly WeekStart,
            string? Referer,
            string CompanyName,
            string CompanyDomain,
            long Total,
            long UniqueOrigins);

        public static IReadOnlyList<Row> Parse(ReadOnlySpan<byte> json)
        {
            var data = ReadDataArray(json);
            var rows = new List<Row>(data.GetArrayLength());

            foreach (var element in data.EnumerateArray())
            {
                var companyName = ReadString(element, "company_name");

                // Defensive: a by-company breakdown only returns attributed
                // traffic, so this has never been observed. A null here would
                // otherwise reach a NOT NULL key column.
                if (string.IsNullOrWhiteSpace(companyName))
                {
                    continue;
                }

                rows.Add(new Row(
                    ReadWeekStart(element),
                    ReadString(element, "referer"),
                    companyName,
                    ReadString(element, "company_domain") ?? string.Empty,
                    ReadCount(element, "total"),
                    ReadCount(element, "unique_origins")));
            }

            return rows;
        }

        /// <summary>
        /// One row of a `breakdown_set=by-version` package export.
        /// </summary>
        public sealed record PackageRow(
            DateOnly WeekStart,
            string PackageName,
            string Version,
            long Downloads,
            long UniqueOrigins);

        /// <summary>
        /// Parses a by-version package export.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Parse"/> because that one skips rows with
        /// no `company_name`, which is every row of this breakdown.
        /// </remarks>
        public static IReadOnlyList<PackageRow> ParsePackageVersions(
            ReadOnlySpan<byte> json)
        {
            var data = ReadDataArray(json);
            var rows = new List<PackageRow>(data.GetArrayLength());

            foreach (var element in data.EnumerateArray())
            {
                var packageName = ReadString(element, "artifact_name");
                var version = ReadString(element, "version");

                // Defensive: every probe row carried both. A null here would
                // otherwise reach a NOT NULL key column.
                if (string.IsNullOrWhiteSpace(packageName) ||
                    string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                rows.Add(new PackageRow(
                    ReadWeekStart(element),
                    packageName,
                    version,
                    ReadCount(element, "total"),
                    ReadCount(element, "unique_origins")));
            }

            return rows;
        }

        /// <summary>
        /// Validates the `{"data":[...]}` envelope and returns the array.
        /// Shared so both entry points reject a malformed body identically.
        /// </summary>
        private static JsonElement ReadDataArray(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                throw new FormatException("Scarf returned an empty response body.");
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Scarf response is not valid JSON.", ex);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Scarf response is not a JSON object.");
            }

            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Scarf response has no data array.");
            }

            return data;
        }

        private static DateOnly ReadWeekStart(JsonElement element)
        {
            var text = ReadString(element, "date")
                ?? throw new FormatException("Scarf row has no date.");

            // Parsed exactly, never through DateTime.Parse, which would attach
            // the host's local offset and can shift the calendar day.
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                throw new FormatException($"Scarf row has an unparseable date '{text}'.");
            }

            return date;
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static long ReadCount(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                throw new FormatException($"Scarf row has no '{name}'.");
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.Null => 0L,
                _ => throw new FormatException(
                    $"Scarf '{name}' is {value.ValueKind}, expected a number."),
            };
        }
    }
}
