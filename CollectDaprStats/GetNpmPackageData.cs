using System.Net;
using System.Net.Http.Json;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetNpmPackageData : WorkflowActivity<NpmPackageInput, bool>
    {
        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;

        public GetNpmPackageData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        private const string TableName = "npm_dapr_dapr";

        private static readonly string[] KeyColumns =
            ["collection_week", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count", "collected_over_number_of_days"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) " +
            "values ($1, $2, $3, $4, $5) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            NpmPackageInput input)
        {
            _httpClient.BaseAddress = new Uri("https://api.npmjs.org/");
            var packageName = WebUtility.UrlEncode(input.PackageName);
            var response = await _httpClient.GetAsync($"versions/{packageName}/last-week");
            if (response.IsSuccessStatusCode)
            {
                var npmPackageVersionResponse = await response.Content.ReadFromJsonAsync<NpmPackageVersionResponse>();
                foreach (var versionPair in npmPackageVersionResponse.Downloads)
                {
                    var npmPackageVersionData = new NpmPackageVersionData
                    (
                        CollectionDate: DateTime.UtcNow,
                        PackageName: npmPackageVersionResponse.Package,
                        PackageVersion: versionPair.Key,
                        Downloads: versionPair.Value,
                        CollectedOverNumberOfDays: 7
                    );

                    Console.WriteLine($"NPM Package: {npmPackageVersionData.PackageName}, Version: {npmPackageVersionData.PackageVersion}, Downloads: {npmPackageVersionData.Downloads}");

                    if (!input.SkipStorage)
                    {
                        var sqlParameters = new object[] { npmPackageVersionData.PackageName, npmPackageVersionData.CollectionDate, npmPackageVersionData.PackageVersion, npmPackageVersionData.Downloads, npmPackageVersionData.CollectedOverNumberOfDays };
                        await _output.InsertAsync(InsertSql, sqlParameters);
                    }
                }

                return true;
            }

            return false;
        }
    }
}


public record NpmPackageInput(string PackageName, bool SkipStorage);
public record NpmPackageVersionResponse(string Package, Dictionary<string, int> Downloads);
public record NpmPackageVersionData(string PackageName, string PackageVersion, long? Downloads, DateTime CollectionDate, int CollectedOverNumberOfDays);