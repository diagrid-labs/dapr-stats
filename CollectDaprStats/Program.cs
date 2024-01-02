
using Discord.Rest;
using Dapr.Workflow;
using Dapr.Client;
using DaprStats;
using Octokit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IGitHubClient>(
    new GitHubClient(
        new ProductHeaderValue("dapr-stats")
    ));
builder.Services.AddSingleton<DiscordRestClient>();
builder.Services.AddSingleton(_ => new DaprClientBuilder().Build());
builder.Services.AddSingleton<PostgresOutput>();
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<CollectorWorkflow>();
    options.RegisterWorkflow<GitHubCollectorWorkflow>();
    options.RegisterActivity<GetNuGetPackageData>();
    options.RegisterActivity<GetNpmPackageData>();
    options.RegisterActivity<GetPythonPackageData>();
    options.RegisterActivity<GetDiscordData>();
    options.RegisterActivity<GetGitHubData>();
});

// Dapr uses a random port for gRPC by default. If we don't know what that port
// is (because this app was started separate from dapr), then assume 50001.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DAPR_GRPC_PORT")))
{
    Environment.SetEnvironmentVariable("DAPR_GRPC_PORT", "50001");
}

var app = builder.Build();

app.Run();

