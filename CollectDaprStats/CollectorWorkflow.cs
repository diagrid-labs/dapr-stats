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
                var nugetPackages = new[]
                {
                    "Dapr.Client",
                    "Dapr.Workflow",
                    "Dapr.AspNetCore"
                };
                var getNugetPackageDataTasks = new List<Task>();
                foreach (var package in nugetPackages)
                {
                    getNugetPackageDataTasks.Add(context.CallActivityAsync(
                        nameof(GetNuGetPackageData),
                        package));
                }
                await Task.WhenAll(getNugetPackageDataTasks);
            }

            if (input.CollectNpmData)
            {
                await context.CallActivityAsync(
                    nameof(GetNpmPackageData),
                    "@dapr/dapr");
            }

            if (input.CollectPythonData)
            {
                var pythonPackages = new[]
                {
                    "dapr",
                    "dapr-agents",
                    "dapr-ext-workflow"
                };
                var getPythonPackageDataTasks = new List<Task>();
                foreach (var package in pythonPackages)
                {
                    getPythonPackageDataTasks.Add(context.CallActivityAsync(
                        nameof(GetPythonPackageData),
                        package));
                }
                await Task.WhenAll(getPythonPackageDataTasks);
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
