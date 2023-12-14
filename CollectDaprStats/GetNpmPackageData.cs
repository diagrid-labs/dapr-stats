using System.Net;
using System.Net.Http.Json;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetNpmPackageData : WorkflowActivity<string, bool>
    {
        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;

        public GetNpmPackageData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            string packageName)
        {

            _httpClient.BaseAddress = new Uri("https://api.npmjs.org/");
            packageName = WebUtility.UrlEncode(packageName);
            var response = await _httpClient.GetAsync($"versions/{packageName}/last-week");
            if (response.IsSuccessStatusCode)
            {
                var npmPackageVersionResponse = await response.Content.ReadFromJsonAsync<NpmPackageVersionResponse>();
                foreach (var versionPair in npmPackageVersionResponse.Downloads)
                {
                    var npmPackageVersionData = new NpmPackageVersionData
                    {
                        CollectionDate = DateTime.UtcNow,
                        PackageName = npmPackageVersionResponse.Package,
                        PackageVersion = versionPair.Key,
                        Downloads = versionPair.Value,
                        CollectedOverNumberOfWeeks = 1
                    };
                    
                    const string tableName = "npm_dapr_dapr";
                    var sqlText = $"insert into {tableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_weeks) values ($1, $2, $3, $4, $5)";
                    var sqlParameters = new object[] { npmPackageVersionData.PackageName, npmPackageVersionData.CollectionDate, npmPackageVersionData.PackageVersion, npmPackageVersionData.Downloads, npmPackageVersionData.CollectedOverNumberOfWeeks};

                    await _output.InsertAsync(sqlText, sqlParameters);
                }

                return true;
            }
            else
            {
                return false;
            }
        }
    }

    public class NpmPackageVersionResponse
    {
        public string Package { get; set; }
        public Dictionary<string, int> Downloads { get; set; }
    }

    public class NpmPackageVersionData
    {
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
        public long? Downloads { get; set; }
        public DateTime CollectionDate { get ; set; }
        public short CollectedOverNumberOfWeeks { get; set; }
    }
}