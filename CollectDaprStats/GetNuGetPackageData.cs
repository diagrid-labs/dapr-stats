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

        // The latest collection week before the one $2 falls in, so a re-run
        // within a week compares against last week rather than against itself.
        internal static readonly string PreviousWeekTotalSql =
            $"select sum(download_count)::bigint from {TableName} " +
            "where package_name = $1 and collection_week = " +
            $"(select max(collection_week) from {TableName} " +
            "where package_name = $1 and collection_week < date_trunc('week', $2::timestamp)::date)";

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

            var collectionDate = DateTime.UtcNow;

            // Checked on dry runs too, so a dry run shows whether a real run would
            // store stale counts. Not storing leaves the package missing, so the
            // workflow retries it after the backoff in case a host has caught up.
            var previousWeekTotal = NuGetSearch.ParsePreviousWeekTotal(
                await _output.QueryAsync(PreviousWeekTotalSql, [package.PackageId, collectionDate]));
            if (NuGetSearch.IsStale(package.TotalDownloads, previousWeekTotal))
            {
                // NUGET_STALE is grepped by run-workflow.yaml to fail the job.
                Console.WriteLine($"NUGET_STALE {package.PackageId}: freshest host total {package.TotalDownloads} is not above the previous week's {previousWeekTotal}; not storing");
                return true;
            }

            foreach (var version in package.Versions)
            {
                var nugetPackageVersionData = new NuGetPackageVersionData
                (
                    CollectionDate: collectionDate,
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
