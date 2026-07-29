using Dapr.Workflow;

namespace DaprStats
{
    public class CheckNuGetPackageData : WorkflowActivity<CheckNuGetPackageDataInput, string[]>
    {
        private const string ViewName = "nuget_dapr_client_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckNuGetPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckNuGetPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"NuGet packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"NuGet packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckNuGetPackageDataInput(NuGetPackageInput[] Packages, DateTime CollectionDate);
}
