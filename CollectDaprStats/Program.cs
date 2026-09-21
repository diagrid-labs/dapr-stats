
using Discord.Rest;
using Dapr.Workflow;
using Dapr.Client;
using DaprStats;
using Octokit;

var daprClient = new DaprClientBuilder().Build();
const string secretStore = "secretstore";
const string DaprStatsGitHubPATKey = "DAPRSTATSGITHUBPAT";

// The sidecar only serves its APIs once every component has initialized, and the
// postgres binding waits on a Neon cold start. Without this the first secret call
// races daprd and the app dies on "Connection refused" before it ever starts.
using var sidecarWait = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await daprClient.WaitForSidecarAsync(sidecarWait.Token);

var ghPATDictionary = await daprClient.GetSecretAsync(secretStore, DaprStatsGitHubPATKey);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IGitHubClient>(_ => {
    var ghClient = new GitHubClient(new ProductHeaderValue("dapr-stats"))
    { 
        Credentials = new Credentials(ghPATDictionary[DaprStatsGitHubPATKey], AuthenticationType.Bearer) 

    };
    return ghClient;
});

builder.Services.AddSingleton<DiscordRestClient>();
builder.Services.AddSingleton(daprClient);
builder.Services.AddSingleton<PostgresOutput>();
builder.Services.AddSingleton<ScarfExportClient>();
builder.Services.AddSingleton<PackageDataChecker>();
builder.Services.AddDaprWorkflow();

// Dapr uses a random port for gRPC by default. If we don't know what that port
// is (because this app was started separate from dapr), then assume 50001.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DAPR_GRPC_PORT")))
{
    Environment.SetEnvironmentVariable("DAPR_GRPC_PORT", "50001");
}

var app = builder.Build();

app.Run();

