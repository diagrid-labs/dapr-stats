
using Discord.Rest;
using Dapr;
using CollectDaprStats;
using Dapr.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<DiscordRestClient>();
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<CollectorWorkflow>();

    options.RegisterActivity<GetNuGetPackageData>();
    // options.RegisterActivity<GetNpmPackageData>();
    // options.RegisterActivity<GetDiscordData>();
});

// Dapr uses a random port for gRPC by default. If we don't know what that port
// is (because this app was started separate from dapr), then assume 50001.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DAPR_GRPC_PORT")))
{
    Environment.SetEnvironmentVariable("DAPR_GRPC_PORT", "50001");
}

var app = builder.Build();

app.Run();

