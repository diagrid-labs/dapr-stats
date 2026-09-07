using System.Globalization;
using System.Text;
using Dapr.Client;

namespace DaprStats
{
    /// <summary>
    /// One place to call Scarf's aggregation export, for every account and
    /// every breakdown.
    /// </summary>
    /// <remarks>
    /// The owner slug and the API token secret are per-request rather than
    /// compile-time constants, which is what lets a second Scarf account
    /// (Diagrid) be collected alongside the first (Dapr).
    /// </remarks>
    public sealed record ScarfExportRequest(
        string Owner,            // "Dapr" | "Diagrid" — case-sensitive.
        string ApiTokenSecret,   // "SCARF_DAPR_API_TOKEN" | "SCARF_DIAGRID_API_TOKEN"
        string[] PixelIds,
        DateOnly From,
        DateOnly To,             // Exclusive, verified 2026-09-01.
        string? Breakdown,       // "by-company", or null to omit.
        string? BreakdownSet,    // "by-referer,by-company", or null to omit.
        bool? GroupByArtifact);  // null omits the parameter entirely.

    public sealed class ScarfExportClient
    {
        private const string SecretStore = "secretstore";

        private readonly HttpClient _httpClient;
        private readonly DaprClient _daprClient;

        public ScarfExportClient(
            IHttpClientFactory httpClientFactory,
            DaprClient daprClient)
        {
            _httpClient = httpClientFactory.CreateClient();
            _daprClient = daprClient;
        }

        /// <summary>
        /// Builds the export URL. Pure and internal so the exact query shape
        /// can be asserted in tests without an HTTP call.
        /// </summary>
        internal static string BuildUrl(ScarfExportRequest request)
        {
            var url = new StringBuilder("https://api.scarf.sh/v3/insights/");

            // Never case-normalised: /v3/* returns 404
            // {"detail":"Organization not found"} unless the slug matches
            // exactly. Confirmed 2026-09-01.
            url.Append(request.Owner);
            url.Append("/aggregations/export");

            url.Append("?start_date=").Append(Iso(request.From));
            url.Append("&end_date=").Append(Iso(request.To));

            foreach (var pixelId in request.PixelIds)
            {
                url.Append("&tracking_pixel_id=").Append(Uri.EscapeDataString(pixelId));
            }

            url.Append("&rollup=weekly");

            if (request.Breakdown is { } breakdown)
            {
                url.Append("&breakdown=").Append(breakdown);
            }

            if (request.BreakdownSet is { } breakdownSet)
            {
                // The comma is left unescaped: this is the exact form verified
                // against the live API on 2026-09-01.
                url.Append("&breakdown_set=").Append(breakdownSet);
            }

            // Asymmetric by design and not to be "tidied": by-company sends
            // false so the pixels merge server-side and unique_origins is not
            // double-counted; by-referer omits the parameter altogether.
            if (request.GroupByArtifact is { } groupByArtifact)
            {
                url.Append("&group_by_artifact=")
                   .Append(groupByArtifact ? "true" : "false");
            }

            url.Append("&format=json");

            return url.ToString();
        }

        public async Task<IReadOnlyList<ScarfAggregationResponse.Row>> FetchAsync(
            ScarfExportRequest request)
        {
            var secrets = await _daprClient.GetSecretAsync(
                SecretStore, request.ApiTokenSecret);
            var token = secrets[request.ApiTokenSecret];

            using var message = new HttpRequestMessage(
                HttpMethod.Get, BuildUrl(request));
            message.Headers.Add("Authorization", $"Bearer {token}");

            var response = await _httpClient.SendAsync(message);
            var payload = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Scarf returned {(int)response.StatusCode}: " +
                    Encoding.UTF8.GetString(payload));
            }

            return ScarfAggregationResponse.Parse(payload);
        }

        private static string Iso(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
