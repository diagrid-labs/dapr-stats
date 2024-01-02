using Dapr.Workflow;
using Octokit;

namespace DaprStats
{
    public class GetGitHubData : WorkflowActivity<GitHubDataInput, bool>
    {
        private readonly PostgresOutput _output;
        private readonly IGitHubClient _gitHubClient;

        public GetGitHubData(
            IGitHubClient gitHubClient,
            PostgresOutput output)
        {
            _gitHubClient = gitHubClient;
            _output = output;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            GitHubDataInput input)
        {
            const int CollectionPeriodInDays = 7;
            var apiOptions = new ApiOptions { PageSize = 200, StartPage = 1 };
            var repository = await _gitHubClient.Repository.Get("dapr", input.Repository);
            var commitRequest = new CommitRequest {Since = input.CollectionDate.AddDays(-CollectionPeriodInDays)};
            var commitList = await _gitHubClient.Repository.Commit.GetAll(repository.Id, commitRequest, apiOptions);
            var commitCountOverPeriod = commitList.Count;
            var commitShas = string.Join(',', commitList.Select(commit => commit.Sha));
            var commitUserNames = string.Join(',', commitList.Select(commit => commit.User.Name).Distinct());
            Console.WriteLine($"Repo: {repository.Name}, Commits: {commitCountOverPeriod}, Shas: {commitShas}, Users: {commitUserNames}");

            var issueRequest = new RepositoryIssueRequest {
                Since = input.CollectionDate.AddDays(-CollectionPeriodInDays),
                Filter = IssueFilter.All,
                SortProperty = IssueSort.Updated,
                SortDirection = SortDirection.Descending};
            var issueList = await _gitHubClient.Issue.GetAllForRepository(repository.Id, issueRequest, apiOptions);
            var issueCountOverPeriod = issueList.Count;
            var issueNumbers = string.Join(',', issueList.Select(issue => issue.Number));
            var issueUserNames = string.Join(',', issueList.Select(issue => issue.User.Name).Distinct());

            Console.WriteLine($"Repo: {repository.Name}, Issues: {issueCountOverPeriod}, Numbers: {issueNumbers}, Users: {issueUserNames}");

            var prRequest = new PullRequestRequest { SortProperty = PullRequestSort.Created, SortDirection = SortDirection.Descending};
            var prList = await _gitHubClient.Repository.PullRequest.GetAllForRepository(repository.Id, prRequest, apiOptions);
            
            var prFilter =  new Func<PullRequest, bool>( pr =>
                pr.CreatedAt > input.CollectionDate.AddDays(-CollectionPeriodInDays) ||
                pr.UpdatedAt > input.CollectionDate.AddDays(-CollectionPeriodInDays) ||
                pr.MergedAt > input.CollectionDate.AddDays(-CollectionPeriodInDays) ||
                pr.ClosedAt > input.CollectionDate.AddDays(-CollectionPeriodInDays));
            var filteredPrList = prList.Where(prFilter);
            var filteredPrUserNames = string.Join(',', filteredPrList.Select(pr => pr.User.Name).Distinct());
            var filteredPrCountOverPeriod = filteredPrList.Count();
            var filteredPrNumbers = string.Join(',', filteredPrList.Select(pr => pr.Number));

            Console.WriteLine($"Repo: {repository.Name}, PRs: {filteredPrCountOverPeriod}, Numbers: {filteredPrNumbers}, Users: {filteredPrUserNames}");

            var githubData = new GitHubDataOutput
            {
                CollectionDate = DateTime.UtcNow,
                Repository = repository.Name,
                ForksTotalCount = repository.ForksCount,
                StarsTotalCount = repository.StargazersCount,
                CommitCount = commitCountOverPeriod,
                CommitUsers = commitUserNames,
                IssueCount = issueCountOverPeriod,
                IssueUsers = issueUserNames,
                PullRequestCount = filteredPrCountOverPeriod,
                PullRequestUsers = filteredPrUserNames,
                CollectedOverNumberOfDays = CollectionPeriodInDays
            };

            string tableName = $"github_dapr";
            var sqlText = $"insert into {tableName} (repo_name, collection_date, fork_count_total, star_count_total, commit_count, commit_users, issue_count, issue_users, pullrequest_count, pullrequest_users, collected_over_number_of_days) values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
            var sqlParameters = new object[] { githubData.Repository, githubData.CollectionDate, githubData.ForksTotalCount, githubData.StarsTotalCount, githubData.CommitCount, githubData.CommitUsers, githubData.IssueCount, githubData.IssueUsers, githubData.PullRequestCount, githubData.PullRequestUsers, githubData.CollectedOverNumberOfDays };
            await _output.InsertAsync(sqlText, sqlParameters);
            return true;
        }
    }

    public record GitHubDataInput(DateTime CollectionDate, string Repository);

    public class GitHubDataOutput
    {
        public string Repository { get; set; }
        public int StarsTotalCount { get; set; }
        public int ForksTotalCount { get; set; }
        public int PullRequestCount { get; set; }
        public string PullRequestUsers { get; set; }
        public int IssueCount { get; set; }
        public string IssueUsers { get; set; }
        public int CommitCount { get; set; }
        public string CommitUsers { get; set; }
        public DateTime CollectionDate { get; set; }
        public int CollectedOverNumberOfDays { get; set; }
    }
}