using Dapr.Workflow;
using Octokit;

namespace DaprStats
{
    public class GetGitHubRepoData : WorkflowActivity<GitHubDataInput, bool>
    {
        private readonly PostgresOutput _output;
        private readonly IGitHubClient _gitHubClient;
        private readonly ILogger _logger;

        public GetGitHubRepoData(
            IGitHubClient gitHubClient,
            PostgresOutput output,
            ILoggerFactory loggerFactory)
        {
            _gitHubClient = gitHubClient;
            _output = output;
            _logger = loggerFactory.CreateLogger<GetGitHubRepoData>();
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            GitHubDataInput input)
        {
            try 
            {
                const int CollectionPeriodInDays = 15;
                var apiOptions = new ApiOptions { PageSize = 250, StartPage = 1 };
                var repository = await _gitHubClient.Repository.Get(input.Organization, input.Repository);
                Console.WriteLine($"Collecting data for {repository.Name}");
                
                var commitOutput = await GetCommitData(input, repository, CollectionPeriodInDays, apiOptions);
                var issueOutput = await GetIssueData(input, repository, CollectionPeriodInDays, apiOptions);
                var commentOutput = await GetCommentData(input, repository, CollectionPeriodInDays, apiOptions);
                var pullRequestOutput = await GetPullRequestData(input, repository, CollectionPeriodInDays, apiOptions);
                var distinctUserNames = commitOutput.CommitUsers.Union(issueOutput.IssueUsers).Union(commentOutput.CommentUsers).Union(pullRequestOutput.PullRequestUsers);

            if (!input.SkipStorage)
            {
                var githubData = new GitHubDataOutput
                {
                    CollectionDate = DateTime.UtcNow,
                    Repository = repository.Name,
                    ForksTotalCount = repository.ForksCount,
                    StarsTotalCount = repository.StargazersCount,
                    CommitCount = commitOutput.CommitCount,
                    CommitUsers = commitOutput.JoinedCommitUsers,
                    IssueCount = issueOutput.IssueCount,
                    IssueUsers = issueOutput.JoinedIssueUsers,
                    CommentCount = commentOutput.CommentCount,
                    CommentUsers = commentOutput.JoinedCommentUsers,
                    PullRequestCount = pullRequestOutput.PullRequestCount,
                    PullRequestUsers = pullRequestOutput.JoinedPullRequestUsers,
                    DistinctUserCount = distinctUserNames.Count(),
                    CollectedOverNumberOfDays = CollectionPeriodInDays
                };

                string tableName = $"github_dapr";
                var sqlText = $"insert into {tableName} (repo_name, collection_date, fork_count_total, star_count_total, commit_count, commit_users, issue_count, issue_users, comment_count, comment_users, pullrequest_count, pullrequest_users, distinct_user_count, collected_over_number_of_days) values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)";
                var sqlParameters = new object[] { githubData.Repository, githubData.CollectionDate, githubData.ForksTotalCount, githubData.StarsTotalCount, githubData.CommitCount, githubData.CommitUsers, githubData.IssueCount, githubData.IssueUsers, githubData.CommentCount, githubData.CommentUsers, githubData.PullRequestCount, githubData.PullRequestUsers, githubData.DistinctUserCount, githubData.CollectedOverNumberOfDays };
                await _output.InsertAsync(sqlText, sqlParameters);
            }

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return true;
        }

        private async Task<CommitOutput> GetCommitData(GitHubDataInput input, Repository repository, int collectionPeriodInDays, ApiOptions apiOptions)
        {
            try
            {
                var commitRequest = new CommitRequest { Since = input.CollectionDate.AddDays(-collectionPeriodInDays) };
                var commitList = await _gitHubClient.Repository.Commit.GetAll(repository.Id, commitRequest, apiOptions);
                var commitCountOverPeriod = commitList.Count;
                var commitShas = string.Join(',', commitList.Select(commit => commit.Sha));
                // List the distinct user names for the commits
                var distinctCommitUserNames = commitList.Select(commit => commit.Author?.Login).Distinct();
                var joinedDistinctCommitUserNames = string.Join(',', distinctCommitUserNames);
                Console.WriteLine($"Repo: {repository.Name}, Commits: {commitCountOverPeriod}, Shas: {commitShas}, Users: {joinedDistinctCommitUserNames}");

                return new CommitOutput(
                    commitCountOverPeriod,
                    distinctCommitUserNames,
                    joinedDistinctCommitUserNames);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting commit data for {Repository}: {Exception}", repository.Name, ex);
                throw;
            }
        }

        private async Task<IssueOutput> GetIssueData(GitHubDataInput input, Repository repository, int collectionPeriodInDays, ApiOptions apiOptions)
        {
            try
            {
                var issueRequest = new RepositoryIssueRequest
                {
                    Since = input.CollectionDate.AddDays(-collectionPeriodInDays),
                    Filter = IssueFilter.All,
                    SortProperty = IssueSort.Updated,
                    SortDirection = SortDirection.Descending
                };
                var issueList = await _gitHubClient.Issue.GetAllForRepository(repository.Id, issueRequest, apiOptions);

                var issueNumbers = string.Join(',', issueList.Select(issue => issue.Number));
                var distinctIssueUserNames = issueList.Select(issue => issue.User.Login).Distinct();
                var joinedDistinctIssueUserNames = string.Join(',', distinctIssueUserNames);
                Console.WriteLine($"Repo: {repository.Name}, Issues: {issueList.Count}, Numbers: {issueNumbers}, Users: {joinedDistinctIssueUserNames}");

                return new IssueOutput
                (
                    issueList.Count,
                    distinctIssueUserNames,
                    joinedDistinctIssueUserNames);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting issue data for {Repository}: {Exception}", repository.Name, ex);
                throw;
            }
        }

        private async Task<CommentOutput> GetCommentData(GitHubDataInput input, Repository repository, int collectionPeriodInDays, ApiOptions apiOptions)
        {
            try
            {
                var commentRequest = new IssueCommentRequest
                {
                    Since = input.CollectionDate.AddDays(-collectionPeriodInDays),
                    Sort = IssueCommentSort.Created,
                    Direction = SortDirection.Descending
                };
                var commentList = await _gitHubClient.Issue.Comment.GetAllForRepository(repository.Id, commentRequest, apiOptions);
                var distinctCommentUserNames = commentList.Select(comment => comment.User.Login).Distinct();
                var joinedDistinctCommentUserNames = string.Join(',', distinctCommentUserNames);
                Console.WriteLine($"Repo: {repository.Name}, Comments: {commentList.Count}, Users: {joinedDistinctCommentUserNames}");

                return new CommentOutput
                (
                    commentList.Count,
                    distinctCommentUserNames,
                    joinedDistinctCommentUserNames);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting comment data for {Repository}: {Exception}", repository.Name, ex);
                throw;
            }
        }

        private async Task<PullRequestOutput> GetPullRequestData(GitHubDataInput input, Repository repository, int collectionPeriodInDays, ApiOptions apiOptions)
        {
            try
            {
                var prRequest = new PullRequestRequest { SortProperty = PullRequestSort.Created, SortDirection = SortDirection.Descending };
                var prList = await _gitHubClient.Repository.PullRequest.GetAllForRepository(repository.Id, prRequest, apiOptions);
                var prFilter = new Func<PullRequest, bool>(pr =>
                    pr.CreatedAt > input.CollectionDate.AddDays(-collectionPeriodInDays) ||
                    pr.UpdatedAt > input.CollectionDate.AddDays(-collectionPeriodInDays) ||
                    pr.MergedAt > input.CollectionDate.AddDays(-collectionPeriodInDays) ||
                    pr.ClosedAt > input.CollectionDate.AddDays(-collectionPeriodInDays));
                var filteredPrList = prList.Where(prFilter);
                var distinctFilteredPrUserNames = filteredPrList.Select(pr => pr.User.Login).Distinct();
                var joinedDistinctFilteredPrUserNames = string.Join(',', distinctFilteredPrUserNames);
                var filteredPrCountOverPeriod = filteredPrList.Count();
                var filteredPrNumbers = string.Join(',', filteredPrList.Select(pr => pr.Number));
                Console.WriteLine($"Repo: {repository.Name}, PRs: {filteredPrCountOverPeriod}, Numbers: {filteredPrNumbers}, Users: {joinedDistinctFilteredPrUserNames}");

                return new PullRequestOutput(
                    filteredPrCountOverPeriod,
                    distinctFilteredPrUserNames,
                    joinedDistinctFilteredPrUserNames);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting pull request data for {Repository}: {Exception}", repository.Name, ex);
                throw;
            }
        }
    }

    public record GitHubDataInput(DateTime CollectionDate, string Organization, string Repository, bool SkipStorage);
    public record CommitOutput(int CommitCount, IEnumerable<string> CommitUsers, string JoinedCommitUsers);
    public record IssueOutput(int IssueCount, IEnumerable<string> IssueUsers, string JoinedIssueUsers);
    public record CommentOutput(int CommentCount, IEnumerable<string> CommentUsers, string JoinedCommentUsers);
    public record PullRequestOutput(int PullRequestCount, IEnumerable<string> PullRequestUsers, string JoinedPullRequestUsers);

    public class GitHubDataOutput
    {
        public required string Repository { get; set; }
        public int StarsTotalCount { get; set; }
        public int ForksTotalCount { get; set; }
        public int PullRequestCount { get; set; }
        public string? PullRequestUsers { get; set; }
        public int IssueCount { get; set; }
        public string? IssueUsers { get; set; }
        public int CommentCount { get; set; }
        public string? CommentUsers { get; set; }
        public int CommitCount { get; set; }
        public string? CommitUsers { get; set; }
        public DateTime CollectionDate { get; set; }
        public int DistinctUserCount { get; set; }
        public int CollectedOverNumberOfDays { get; set; }
    }
}