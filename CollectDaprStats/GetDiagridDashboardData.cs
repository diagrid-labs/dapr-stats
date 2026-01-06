using Dapr.Workflow;
using HtmlAgilityPack;
using System.Globalization;

namespace DaprStats
{
    public class GetDiagridDashboardData : WorkflowActivity<DiagridDashboardInput, bool>
    {
        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;

        public GetDiagridDashboardData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            DiagridDashboardInput input)
        {
            const string url = "https://github.com/orgs/diagridio/packages/container/package/diagrid-dashboard";
            
            try
            {
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to retrieve Diagrid Dashboard page. Status: {response.StatusCode}");
                    return false;
                }
                
                var html = await response.Content.ReadAsStringAsync();
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                
                // Extract download count from the page
                // Looking for the downloads section in GitHub container registry page
                var downloadNode = htmlDoc.DocumentNode.SelectSingleNode("//div[@class='lh-condensed d-flex flex-column flex-items-baseline pr-1']/h3");
                
                if (downloadNode == null)
                {
                    Console.WriteLine("Failed to find download count element on Diagrid Dashboard page.");
                    return false;
                }
                
                var downloadText = downloadNode.GetAttributeValue("title", string.Empty);
                if (string.IsNullOrWhiteSpace(downloadText))
                {
                    downloadText = downloadNode.InnerText.Trim();
                }
                
                var downloadCount = ParseDownloadCount(downloadText);
                
                if (downloadCount == 0)
                {
                    Console.WriteLine($"Failed to parse download count from text: '{downloadText}'");
                    return false;
                }
                
                var data = new DiagridDashboardData(
                    CollectionDate: DateTime.UtcNow,
                    DownloadCount: downloadCount
                );
                
                Console.WriteLine($"Diagrid Dashboard downloads: {data.DownloadCount:N0}");
                
                if (!input.SkipStorage)
                {
                    const string tableName = "diagrid_dashboard";
                    var sqlText = $"insert into {tableName} (collection_date, download_count) values ($1, $2)";
                    var sqlParameters = new object[] { data.CollectionDate, data.DownloadCount };
                    await _output.InsertAsync(sqlText, sqlParameters);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving Diagrid Dashboard data: {ex.Message}");
                return false;
            }
        }

        private static long ParseDownloadCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            // Remove common formatting characters
            text = text.Trim().Replace(",", "").Replace(" ", "");

            // Handle suffixes like k (thousands), m (millions), b (billions)
            var multiplier = 1L;
            if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1_000;
                text = text[..^1];
            }
            else if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1_000_000;
                text = text[..^1];
            }
            else if (text.EndsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1_000_000_000;
                text = text[..^1];
            }

            // Parse the number
            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return (long)(number * multiplier);
            }

            return 0;
        }
    }

    public record DiagridDashboardInput(bool SkipStorage);
    public record DiagridDashboardData(DateTime CollectionDate, long DownloadCount);
}
