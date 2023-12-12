﻿/*
 * This function is not intended to be invoked directly. Instead it will be
 * triggered by an orchestrator function.
 * 
 * Before running this sample, please:
 * - create a Durable orchestration function
 * - create a Durable HTTP starter function
 * - run 'npm install durable-functions' from the wwwroot folder of your
 *   function app in Kudu
 */

import { AzureFunction, Context } from "@azure/functions"

const activityFunction: AzureFunction = async function (context: Context): Promise<string> {
    let url = `https://azuresearch-usnc.nuget.org/query?q=PackageId:${context.bindings.packageName}`;
    let response = await fetch(url);
    let data =  await response.json();
    return data.versions;
};

export default activityFunction;
