using Dapr.Workflow;

namespace DaprStats
{
    public class GetNuGetPackageData : WorkflowActivity<NuGetPackageInput, bool>
    {
        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;

        public GetNuGetPackageData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        private const string TableName = "nuget_dapr_client";

        private static readonly string[] KeyColumns =
            ["collection_week", "package_name", "package_version"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "download_count"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (package_name, collection_date, package_version, download_count) " +
            "values ($1, $2, $3, $4) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

        public override async Task<bool> RunAsync(WorkflowActivityContext context, NuGetPackageInput input)
        {
            var serviceIndex = await _httpClient.GetByteArrayAsync(NuGetSearch.ServiceIndexUrl);
            var endpoints = NuGetSearch.SearchEndpoints(serviceIndex);

            var results = new List<NuGetSearch.SearchResult?>();
            foreach (var endpoint in endpoints)
            {
                var result = await SearchAsync(endpoint, input.PackageName);
                Console.WriteLine($"NuGet search {endpoint.Host}: {input.PackageName} = {result?.TotalDownloads.ToString() ?? "no result"}");
                results.Add(result);
            }

            var package = NuGetSearch.PickFreshest(results)
                ?? throw new InvalidOperationException(
                    $"No NuGet search endpoint returned {input.PackageName} ({string.Join(", ", endpoints.Select(e => e.Host))})");

            foreach (var version in package.Versions)
            {
                var nugetPackageVersionData = new NuGetPackageVersionData
                (
                    CollectionDate: DateTime.UtcNow,
                    PackageName: package.PackageId,
                    PackageVersion: version.Version,
                    Downloads: version.Downloads
                );
                Console.WriteLine($"NuGet Package: {nugetPackageVersionData.PackageName}, Version: {nugetPackageVersionData.PackageVersion}, Downloads: {nugetPackageVersionData.Downloads}");
                
                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { nugetPackageVersionData.PackageName, nugetPackageVersionData.CollectionDate, nugetPackageVersionData.PackageVersion, nugetPackageVersionData.Downloads};
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }
            }

            return true;
        }

        // One failing host must not lose the package when another can answer,
        // so a failure here is logged and treated as "no result".
        private async Task<NuGetSearch.SearchResult?> SearchAsync(Uri endpoint, string packageName)
        {
            try
            {
                var response = await _httpClient.GetAsync(NuGetSearch.BuildQueryUri(endpoint, packageName));
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"NuGet search {endpoint.Host} returned {(int)response.StatusCode} for {packageName}");
                    return null;
                }
                return NuGetSearch.ParseSearchResult(await response.Content.ReadAsByteArrayAsync());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"NuGet search {endpoint.Host} failed for {packageName}: {ex.Message}");
                return null;
            }
        }
    }

    public record NuGetPackageInput(string PackageName, bool SkipStorage);
    public record NuGetPackageVersionData(string PackageName, string PackageVersion, long? Downloads, DateTime CollectionDate);

}
