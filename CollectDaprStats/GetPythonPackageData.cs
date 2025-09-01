using Dapr.Workflow;
using System.Net;

namespace DaprStats
{
    public class GetPythonPackageData : WorkflowActivity<string, bool>
    {
        private readonly PostgresOutput _output;
        private readonly HttpClient _httpClient;

        public GetPythonPackageData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        public override async Task<bool> RunAsync(WorkflowActivityContext context, string packageName)
        {
            _httpClient.BaseAddress = new Uri("https://pypistats.org/");
            packageName = WebUtility.UrlEncode(packageName);
            var response = await _httpClient.GetAsync($"api/packages/{packageName}/recent");
            if (response.IsSuccessStatusCode)
            {
                var pypiPackageVersionResponse = await response.Content.ReadFromJsonAsync<PyPiPackageVersionResponse>();

                Console.WriteLine($"Package: {pypiPackageVersionResponse.Package}, Downloads: {pypiPackageVersionResponse.Data["last_month"]}");

                var pythonPackageData = new PythonPackageData
                {
                    CollectionDate = DateTime.UtcNow,
                    PackageName = pypiPackageVersionResponse.Package,
                    PackageVersion = "all",
                    Downloads = pypiPackageVersionResponse.Data["last_month"],
                    CollectedOverNumberOfDays = 30
                };

                const string tableName = "python_dapr";
                var sqlText = $"insert into {tableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) values ($1, $2, $3, $4, $5)";
                var sqlParameters = new object[] { pythonPackageData.PackageName, pythonPackageData.CollectionDate, pythonPackageData.PackageVersion, pythonPackageData.Downloads, pythonPackageData.CollectedOverNumberOfDays };

                await _output.InsertAsync(sqlText, sqlParameters);

                return true;
            }

            return false;
        }
    }

    public class PyPiPackageVersionResponse
    {
        public string Package { get; set; }
        public Dictionary<string, int> Data { get; set; }
    }

    public class PythonPackageData
    {
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
        public long? Downloads { get; set; }
        public DateTime CollectionDate { get; set; }
        public int CollectedOverNumberOfDays { get; set; }
    }
}