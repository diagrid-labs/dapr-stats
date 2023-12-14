using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace Dapr
{
    public static class CollectorStarter
    {
        [Function(nameof(CollectorStarter))]
        public static async Task<HttpResponseData> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext)
        {
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(CollectorOrchestrator));

            return client.CreateCheckStatusResponse(req, instanceId);
        }
    }
}
