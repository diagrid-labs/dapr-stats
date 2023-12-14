using CollectDaprStats;
using Dapr.Workflow;

namespace Dapr
{
    public class CollectorWorkflow : Workflow<DateTime, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            DateTime collectionDate)
        {
            await context.CallActivityAsync<IEnumerable<NuGetPackageVersionData>>(
                nameof(GetNuGetPackageData),
                "Dapr.Client");
            // await context.CallActivityAsync<IEnumerable<NpmPackageVersionData>>(
            //     nameof(GetNpmPackageData),
            //     "@dapr/dapr");
            // await context.CallActivityAsync<DiscordData>(
            //     nameof(GetDiscordData));

            return true;
        }
    }
}
