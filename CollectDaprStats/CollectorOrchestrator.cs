using CollectDaprStats;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace Dapr
{
    public static class CollectorOrchestrator
    {
        [Function(nameof(CollectorOrchestrator))]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            ILogger logger = context.CreateReplaySafeLogger(nameof(CollectorOrchestrator));
            
            await context.CallActivityAsync<IEnumerable<NuGetPackageVersionData>>(
                nameof(GetNuGetPackageData),
                "Dapr.Client");
            await context.CallActivityAsync<IEnumerable<NpmPackageVersionData>>(
                nameof(GetNpmPackageData),
                "@dapr/dapr");
            await context.CallActivityAsync<DiscordData>(
                nameof(GetDiscordData));
        }
    }
}
