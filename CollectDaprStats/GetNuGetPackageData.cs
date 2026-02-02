using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetNuGetPackageData : WorkflowActivity<NuGetPackageInput, bool>
    {
        private readonly PostgresOutput _output;

        public GetNuGetPackageData(PostgresOutput output)
        {
            _output = output;
        }

        public override async Task<bool> RunAsync(WorkflowActivityContext context, NuGetPackageInput input)
        {
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<PackageSearchResource>();
            
            var searchResult = await resource.SearchAsync(
                input.PackageName,
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
                (
                    CollectionDate: DateTime.UtcNow,
                    PackageName: daprClientPackage.Identity.Id,
                    PackageVersion: version.Version.ToFullString(),
                    Downloads: version.DownloadCount
                );
                Console.WriteLine($"NuGet Package: {nugetPackageVersionData.PackageName}, Version: {nugetPackageVersionData.PackageVersion}, Downloads: {nugetPackageVersionData.Downloads}");
                
                if (!input.SkipStorage)
                {
                    const string tableName = "nuget_dapr_client";
                    var sqlText = $"insert into {tableName} (package_name, collection_date, package_version, download_count) values ($1, $2, $3, $4)";
                    var sqlParameters = new object[] { nugetPackageVersionData.PackageName, nugetPackageVersionData.CollectionDate, nugetPackageVersionData.PackageVersion, nugetPackageVersionData.Downloads};
                    await _output.InsertAsync(sqlText, sqlParameters);
                }
            }

            return true;
        }
    }

    public record NuGetPackageInput(string PackageName, bool SkipStorage);
    public record NuGetPackageVersionData(string PackageName, string PackageVersion, long? Downloads, DateTime CollectionDate);

}