using Dapr.Workflow;
using Octokit;

namespace DaprStats
{
    public class GetGitHubReposForOrg : WorkflowActivity<string, string[]>
    {
        private readonly IGitHubClient _gitHubClient;

        public GetGitHubReposForOrg(IGitHubClient gitHubClient)
        {
            _gitHubClient = gitHubClient;
        }

        public override async Task<string[]> RunAsync(
            WorkflowActivityContext context,
            string orgName)
        {
            var apiOptions = new ApiOptions { PageSize = 100, StartPage = 1 };
            var repositories = await _gitHubClient.Repository.GetAllForOrg(orgName, apiOptions);
            var repositoryNames = repositories.Select(repo => repo.Name);
            var repositoryNamesJoined = string.Join(',', repositoryNames);
            Console.WriteLine($"Repository count: {repositories.Count}, Names: {repositoryNamesJoined}");

            return repositoryNames.ToArray();
        }
    }
}