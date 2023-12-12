import { AzureFunction, Context, HttpRequest } from "@azure/functions"
import * as df from "durable-functions"

const httpTrigger: AzureFunction = async function (context: Context, req: HttpRequest): Promise<void> {
    let client = df.getClient(context)
    let instanceId = await client.startNew("CollectorOrchestrator");
    context.res = {
        body: JSON.stringify(instanceId)
    };
};

export default httpTrigger;