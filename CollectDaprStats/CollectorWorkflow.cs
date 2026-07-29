using Dapr.Workflow;

namespace DaprStats
{
    public class CollectorWorkflow : Workflow<CollectorWorkflowInput, bool>
    {
        // The Get -> Check -> Sleep loop body runs at most this many times:
        // the initial pass plus two retries, so at most two backoff sleeps.
        private const int MaxAttempts = 3;

        // Long enough for the upstream package APIs to clear a 429.
        private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);

        public override async Task<bool> RunAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var packageLoops = new List<Task>();

            if (input.NuGetPackageNames.Length > 0)
            {
                packageLoops.Add(CollectNuGetPackagesAsync(context, input));
            }

            if (input.NpmPackageNames.Length > 0)
            {
                packageLoops.Add(CollectNpmPackagesAsync(context, input));
            }

            if (input.PythonPackageNames.Length > 0)
            {
                packageLoops.Add(CollectPythonPackagesAsync(context, input));
            }

            if (packageLoops.Count > 0)
            {
                await Task.WhenAll(packageLoops);
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

        private static async Task CollectNuGetPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.NuGetPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetNuGetPackageData),
                        new NuGetPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(5), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckNuGetPackageData),
                    new CheckNuGetPackageDataInput(
                        pending.Select(p => new NuGetPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
        }

        private static async Task CollectNpmPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.NpmPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetNpmPackageData),
                        new NpmPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(5), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckNpmPackageData),
                    new CheckNpmPackageDataInput(
                        pending.Select(p => new NpmPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
        }

        private static async Task CollectPythonPackagesAsync(
            WorkflowContext context,
            CollectorWorkflowInput input)
        {
            var pending = input.PythonPackageNames;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                foreach (var packageName in pending)
                {
                    await context.CallActivityAsync(
                        nameof(GetPythonPackageData),
                        new PythonPackageInput(packageName, input.SkipStorage));
                    // Wait to prevent 429 error
                    await context.CreateTimer(TimeSpan.FromSeconds(10), CancellationToken.None);
                }

                if (input.SkipStorage)
                {
                    // Nothing was stored, so there is nothing to verify.
                    return;
                }

                pending = await context.CallActivityAsync<string[]>(
                    nameof(CheckPythonPackageData),
                    new CheckPythonPackageDataInput(
                        pending.Select(p => new PythonPackageInput(p, input.SkipStorage)).ToArray(),
                        input.CollectionDate));

                if (pending.Length == 0 || attempt == MaxAttempts)
                {
                    return;
                }

                await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
            }
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
