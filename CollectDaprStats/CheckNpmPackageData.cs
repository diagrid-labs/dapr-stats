using Dapr.Workflow;

namespace DaprStats
{
    public class CheckNpmPackageData : WorkflowActivity<CheckNpmPackageDataInput, string[]>
    {
        private const string ViewName = "npm_dapr_dapr_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckNpmPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckNpmPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"NPM packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"NPM packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckNpmPackageDataInput(NpmPackageInput[] Packages, DateTime CollectionDate);
}
