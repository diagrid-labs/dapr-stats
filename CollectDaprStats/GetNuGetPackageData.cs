using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetNuGetPackageData : WorkflowActivity<string, bool>
    {
        private readonly PostgresOutput _output;

        public GetNuGetPackageData(PostgresOutput output)
        {
            _output = output;
        }

        public override async Task<bool> RunAsync(WorkflowActivityContext context, string packageName)
        {
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<PackageSearchResource>();
            
            var searchResult = await resource.SearchAsync(
                packageName,
                new SearchFilter(false),
                0,
                1,
                NullLogger.Instance,
                CancellationToken.None);
            var daprClientPackage = searchResult.FirstOrDefault();
            var daprClientVersions = await daprClientPackage.GetVersionsAsync();

            foreach (var version in daprClientVersions)
            {
                var nugetPackageVersionData = new NuGetPackageVersionData
                {
                    CollectionDate = DateTime.UtcNow,
                    PackageName = daprClientPackage.Identity.Id,
                    PackageVersion = version.Version.ToFullString(),
                    Downloads = version.DownloadCount,
                    CollectedOverNumberOfDays = 6 * 7
                };
                Console.WriteLine($"Package: {nugetPackageVersionData.PackageName}, Version: {nugetPackageVersionData.PackageVersion}, Downloads: {nugetPackageVersionData.Downloads}");
                
                const string tableName = "nuget_dapr_client";
                var sqlText = $"insert into {tableName} (package_name, collection_date, package_version, download_count, collected_over_number_of_days) values ($1, $2, $3, $4, $5)";
                var sqlParameters = new object[] { nugetPackageVersionData.PackageName, nugetPackageVersionData.CollectionDate, nugetPackageVersionData.PackageVersion, nugetPackageVersionData.Downloads, nugetPackageVersionData.CollectedOverNumberOfDays};
                
                await _output.InsertAsync(sqlText, sqlParameters);
            }

            return true;
        }
    }

    public class NuGetPackageVersionData
    {
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
        public long? Downloads { get; set; }
        public DateTime CollectionDate { get ; set; }
        public int CollectedOverNumberOfDays { get; set; }
    }
}