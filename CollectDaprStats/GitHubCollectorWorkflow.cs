using Dapr.Workflow;

namespace DaprStats
{
    public class GitHubCollectorWorkflow : Workflow<GitHubCollectorWorkflowInput, bool>
    {
        public override async Task<bool> RunAsync(
            WorkflowContext context,
            GitHubCollectorWorkflowInput input)
        {
            var githubTasks = new List<Task<bool>>();

            foreach (var repository in input.Repositories)
            {
                githubTasks.Add(context.CallActivityAsync<bool>(
                    nameof(GetGitHubData),
                    new GitHubDataInput(input.CollectionDate, repository)
                ));
            }

            await Task.WhenAll(githubTasks);

            return true;
        }
    }

    public record GitHubCollectorWorkflowInput(DateTime CollectionDate, string[] Repositories);
}
