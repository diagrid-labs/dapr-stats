using Dapr.Workflow;

namespace DaprStats
{
    public class CollectorWorkflow : Workflow<CollectorWorkflowInput, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            if (input.NuGetPackageNames.Length > 0)
            {
                var getNugetPackageDataTasks = new List<Task>();
                foreach (var nugetPackage in input.NuGetPackageNames)
                {
                    getNugetPackageDataTasks.Add(context.CallActivityAsync(
                        nameof(GetNuGetPackageData),
                        new NuGetPackageInput(nugetPackage, input.SkipStorage)));
                }
                await Task.WhenAll(getNugetPackageDataTasks);
            }

            if (input.NpmPackageNames.Length > 0)
            {
                var getNpmPackageDataTasks = new List<Task>();
                foreach (var npmPackage in input.NpmPackageNames)
                {
                    getNpmPackageDataTasks.Add(context.CallActivityAsync(
                        nameof(GetNpmPackageData),
                        new NpmPackageInput(npmPackage, input.SkipStorage)));
                }
                await Task.WhenAll(getNpmPackageDataTasks);
                
            }

            if (input.PythonPackageNames.Length > 0)
            {
                var getPythonPackageDataTasks = new List<Task>();
                foreach (var pythonPackage in input.PythonPackageNames)
                {
                    getPythonPackageDataTasks.Add(context.CallActivityAsync(
                        nameof(GetPythonPackageData),
                        new PythonPackageInput(pythonPackage, input.SkipStorage)));
                }
                await Task.WhenAll(getPythonPackageDataTasks);
            }

            if (input.DockerHubImages.Length > 0)
            {
                var getDockerHubDataTasks = new List<Task>();
                foreach (var dockerHubImage in input.DockerHubImages)
                {
                    // split the input string into organization and image name
                    var parts = dockerHubImage.Split('/');
                    if (parts.Length == 2)
                    {
                        getDockerHubDataTasks.Add(context.CallActivityAsync(
                            nameof(GetDockerHubData),
                            new DockerHubInput(parts[0], parts[1], input.SkipStorage)));
                    }
                }
                await Task.WhenAll(getDockerHubDataTasks);
            }

            if (input.CollectDiscordData)
            {
                await context.CallActivityAsync(
                    nameof(GetDiscordData),
                    new DiscordInput(input.SkipStorage));
            }

            if (input.CollectDiagridDashboardData)
            {
                await context.CallActivityAsync(
                    nameof(GetDiagridDashboardData),
                    new DiagridDashboardInput(input.SkipStorage));
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
                        repositories,
                        input.SkipStorage));
                }
            }

            return true;
        }
    }

    public record CollectorWorkflowInput(
        DateTime CollectionDate,
        string[] NuGetPackageNames,
        string[] NpmPackageNames,
        string[] PythonPackageNames,
        string[] DockerHubImages,
        bool CollectDiscordData,
        bool CollectGitHubData,
        bool CollectDiagridDashboardData,
        bool SkipStorage);
}
