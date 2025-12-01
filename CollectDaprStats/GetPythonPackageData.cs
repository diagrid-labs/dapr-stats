using Dapr.Workflow;
using System.Net;

namespace DaprStats
{
    public class GetPythonPackageData : WorkflowActivity<PythonPackageInput, bool>
    {
        private readonly PostgresOutput _output;
        private readonly HttpClient _httpClient;

        public GetPythonPackageData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        public override async Task<bool> RunAsync(WorkflowActivityContext context, PythonPackageInput input)
        {
            _httpClient.BaseAddress = new Uri("https://pypistats.org/");
            var packageName = WebUtility.UrlEncode(input.PackageName);
            var response = await _httpClient.GetAsync($"api/packages/{packageName}/recent");
            if (response.IsSuccessStatusCode)
            {
                var pypiPackageVersionResponse = await response.Content.ReadFromJsonAsync<PyPiPackageVersionResponse>();

                Console.WriteLine($"Python Package: {pypiPackageVersionResponse.Package}, Downloads: {pypiPackageVersionResponse.Data["last_month"]}");

                var pythonPackageData = new PythonPackageData
                (
                    CollectionDate: DateTime.UtcNow,
                    PackageName: pypiPackageVersionResponse.Package,
                    PackageVersion: "all",
                    Downloads: pypiPackageVersionResponse.Data["last_month"],
                    CollectedOverNumberOfDays: 30
                );

                const string tableName = "python_dapr";
                var sqlText = $"insert into {tableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) values ($1, $2, $3, $4, $5)";
                var sqlParameters = new object[] { pythonPackageData.PackageName, pythonPackageData.CollectionDate, pythonPackageData.PackageVersion, pythonPackageData.Downloads, pythonPackageData.CollectedOverNumberOfDays };

                await _output.InsertAsync(sqlText, sqlParameters);

                return true;
            }

            return false;
        }
    }

    public record PythonPackageInput(string PackageName, bool SkipStorage);
    public record PyPiPackageVersionResponse(string Package, Dictionary<string, int> Data);
    public record PythonPackageData(string PackageName, string PackageVersion, long? Downloads, DateTime CollectionDate, int CollectedOverNumberOfDays);
}