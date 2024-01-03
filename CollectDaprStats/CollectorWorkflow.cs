using Dapr.Workflow;

namespace DaprStats
{
    public class CollectorWorkflow : Workflow<CollectorWorkflowInput, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            if (input.CollectNuGetData)
            {
                await context.CallActivityAsync(
                    nameof(GetNuGetPackageData),
                    "Dapr.Client");
            }

            if (input.CollectNpmData)
            {
                await context.CallActivityAsync(
                    nameof(GetNpmPackageData),
                    "@dapr/dapr");
            }

            if (input.CollectPythonData)
            {
                await context.CallActivityAsync(
                    nameof(GetPythonPackageData),
                    "dapr");
            }

            if (input.CollectDiscordData)
            {
                await context.CallActivityAsync(
                    nameof(GetDiscordData),
                    string.Empty);
            }

            if (input.CollectGitHubData)
            {
                const string orgName = "dapr";
                var repositories = await context.CallActivityAsync<string[]>(
                    nameof(GetGitHubReposForOrg),
                    orgName);
                Console.WriteLine($"Repository count for {orgName}: {repositories.Length}");

                if (repositories.Length > 0)
                {
                    await context.CallChildWorkflowAsync(
                    nameof(GitHubCollectorWorkflow),
                    new GitHubCollectorWorkflowInput(
                        input.CollectionDate,
                        orgName,
                        repositories));
                }
            }

            return true;
        }
    }

    public record CollectorWorkflowInput(
        DateTime CollectionDate,
        bool CollectNuGetData,
        bool CollectNpmData,
        bool CollectPythonData,
        bool CollectDiscordData,
        bool CollectGitHubData);
}
