using Dapr.Workflow;

namespace DaprStats
{
    public class CollectorWorkflow : Workflow<DateTime, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            DateTime collectionDate)
        {
            await context.CallActivityAsync(
                nameof(GetNuGetPackageData),
                "Dapr.Client");
            await context.CallActivityAsync(
                nameof(GetNpmPackageData),
                "@dapr/dapr");
            await context.CallActivityAsync(
                nameof(GetPythonPackageData),
                "dapr");
            await context.CallActivityAsync(
                nameof(GetDiscordData),
                string.Empty);
            var repositories = new[] {
                "dapr",
                "docs" };
            await context.CallChildWorkflowAsync(
                nameof(GitHubCollectorWorkflow),
                new GitHubCollectorWorkflowInput(
                    collectionDate,
                    repositories));

            return true;
        }
    }
}
