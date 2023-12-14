using Dapr.Workflow;

namespace DaprStats
{
    public class CollectorWorkflow : Workflow<DateTime, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            DateTime collectionDate)
        {
            await context.CallActivityAsync<bool>(
                nameof(GetNuGetPackageData),
                "Dapr.Client");
            await context.CallActivityAsync<bool>(
                nameof(GetNpmPackageData),
                "@dapr/dapr");
            await context.CallActivityAsync<bool>(
                nameof(GetDiscordData),
                string.Empty);

            return true;
        }
    }
}
