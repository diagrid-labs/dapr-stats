using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Azure.Data.Tables;
using Azure;

namespace CollectDaprStats
{
    public class GetNuGetPackageData
    {
        [Function(nameof(GetNuGetPackageData))]
        [TableOutput("NuGetDaprClient", Connection = "AzureWebJobsStorage")]
        public async Task<IEnumerable<NuGetPackageVersionData>> Run(
            [ActivityTrigger] string packageName, FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger(nameof(GetNuGetPackageData));
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
            if (daprClientPackage == null)
            {
                logger.LogError($"Could not find package {packageName}");
            }
            var daprClientVersions = await daprClientPackage.GetVersionsAsync();

            var nugetPackageVersionDataList = new List<NuGetPackageVersionData>();
            foreach (var version in daprClientVersions)
            {
                var nugetPackageVersionData = new NuGetPackageVersionData
                {
                    DayOfYear = DateTime.UtcNow.DayOfYear,
                    PackageName = daprClientPackage.Identity.Id,
                    VersionString = version.Version.ToFullString(),
                    Downloads = version.DownloadCount,
                    PartitionKey = DateTime.UtcNow.DayOfYear.ToString(),
                    RowKey = $"{daprClientPackage.Identity.Id}-{version.Version.ToFullString()}"
                };
                nugetPackageVersionDataList.Add(nugetPackageVersionData);
                logger.LogInformation(nugetPackageVersionData.ToString());
            }

            return nugetPackageVersionDataList;
        }
    }

    public class NuGetPackageVersionData : ITableEntity
    {
        public int DayOfYear { get; set; }
        public string PackageName { get; set; }
        public string? VersionString { get; set; }
        public long? Downloads { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get ; set; }
        public ETag ETag { get; set; }

        public override string ToString()
        {
            return $"{PackageName} {VersionString} {Downloads}";
        }
    }
}