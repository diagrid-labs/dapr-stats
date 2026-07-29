using Dapr.Workflow;

namespace DaprStats
{
    public class CheckPythonPackageData : WorkflowActivity<CheckPythonPackageDataInput, string[]>
    {
        private const string ViewName = "python_dapr_latest_packages_view";

        private readonly PackageDataChecker _checker;

        public CheckPythonPackageData(PackageDataChecker checker)
        {
            _checker = checker;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            CheckPythonPackageDataInput input)
        {
            var collectionDate = PackageDataChecker.ToCollectionDate(input.CollectionDate);
            var expected = input.Packages.Select(p => p.PackageName);

            var missing = await _checker.FindMissingAsync(ViewName, expected, collectionDate);

            Console.WriteLine(missing.Length == 0
                ? $"Python packages: all {input.Packages.Length} stored for {collectionDate:yyyy-MM-dd}"
                : $"Python packages not stored for {collectionDate:yyyy-MM-dd}: {string.Join(", ", missing)}");

            return missing;
        }
    }

    public record CheckPythonPackageDataInput(PythonPackageInput[] Packages, DateTime CollectionDate);
}
