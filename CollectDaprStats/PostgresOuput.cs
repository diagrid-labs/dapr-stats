using System.Globalization;
using System.Text.Json;
using Dapr.Client;

namespace DaprStats
{
    public class PostgresOutput
    {
        private const string BindingName = "daprstats";
        private const string ExecOperation = "exec";
        private const string QueryOperation = "query";

        private readonly DaprClient _daprClient;

        public PostgresOutput(DaprClient daprClient)
        {
            _daprClient = daprClient;
        }

        public async Task InsertAsync(string sqlText, object[] sqlParameters)
        {
            var paramsText = JsonSerializer.Serialize(sqlParameters);
            var metadata = new Dictionary<string, string>
            {
                {"sql", sqlText},
                {"params", paramsText}
            };

            const string data = "";

            await _daprClient.InvokeBindingAsync(BindingName, ExecOperation, data, metadata);
        }

        public async Task<PackageCollectionRecord[]> ReadAsync(string sqlText)
        {
            var request = new BindingRequest(BindingName, QueryOperation);
            request.Metadata.Add("sql", sqlText);

            var response = await _daprClient.InvokeBindingAsync(request);

            return ParseRows(response.Data.Span);
        }

        internal static PackageCollectionRecord[] ParseRows(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty)
            {
                return [];
            }

            var rows = JsonSerializer.Deserialize<JsonElement[][]>(json) ?? [];

            return rows
                .Select(row => new PackageCollectionRecord(row[0].GetString()!, ParseDate(row[1])))
                .ToArray();
        }

        private static DateOnly ParseDate(JsonElement element)
        {
            var text = element.GetString()!;

            // A bare `yyyy-MM-dd` must not go through DateTimeOffset.Parse, which
            // would attach the host's local offset and can shift the calendar day.
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                return date;
            }

            var offset = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            return DateOnly.FromDateTime(offset.UtcDateTime);
        }
    }

    public record PackageCollectionRecord(string PackageName, DateOnly CollectionDate);
}
