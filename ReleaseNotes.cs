using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Octokit;

namespace OctokitReleaseNotes
{
    public class ReleaseNotes
    {
        private IGitHubClient GitHubClient;

        public ReleaseNotes(IGitHubClient client)
        {
            this.GitHubClient = client;
        }

        public async Task<string> GetReleaseNotes(string owner, string repo, string from, string to)
        {
            var sb = new StringBuilder();

            var mergedPulls = await GetMergedPullRequestsBetween2Refs(owner, repo, from, to);
            var groupByMilestone = mergedPulls.Values
                .Where(x => x.Issue.Labels.All(y => y.Name != "skip-release-notes"))
                .GroupBy(x => x.PullRequest.Milestone == null ? "zzzNone" : x.PullRequest.Milestone.Title)
                .OrderBy(x => x.Key.ToUpper());

            foreach (var milestoneGroup in groupByMilestone)
            {
                var pulls = milestoneGroup.OrderBy(x => x.PullRequest.MergedAt.Value).Reverse();
                sb.AppendFormat("\r\n\r\n###Milestone: {0}\r\n", milestoneGroup.Key.Replace("zzzNone", "None"));
                foreach (var pull in pulls)
                {
                    var commiters = new[] { pull.PullRequest.User }
                        .Select(x => FormatContributor(x))
                        .Concat(
                            pull.Contributors
                                .Select(x => FormatContributor(x))
                                .OrderBy(x => x))
                        .Distinct();

                    sb.AppendFormat("\r\n- {0} - [#{1}]({2}) via {3}",
                        pull.PullRequest.Title,
                        pull.PullRequest.Number,
                        pull.PullRequest.HtmlUrl,
                        string.Join(", ", commiters));
                }
            }

            return sb.ToString();
        }

        private object FormatContributor(object contributor)
        {
            var login = "";
            var url = "";
            if (contributor is User)
            {
                var user = contributor as User;
                login = user.Login;
                url = user.HtmlUrl;
            }
            else if (contributor is Author)
            {
                var author = contributor as Author;
                login = author.Login;
                url = author.HtmlUrl;
            }
            else
            {
                return contributor.ToString();
            }

            //return $"[{login}]({url})";
            return $"@{login}";
        }

        // A class containing  the info about a pull request we need to generate release notes
        private class CachedPullRequest
        {
            // The pull request itself
            public PullRequest PullRequest { get; set; }

            // THe issue object
            public Issue Issue { get; set; }

            // The merge commit Sha
            public string MergeCommitSha { get; set; }

            // The commits on the PR
            public IEnumerable<PullRequestCommit> Commits { get; set; }

            // The comments on the PR
            public IEnumerable<IssueComment> Comments { get; set; }

            public IEnumerable<Author> Contributors { get; set; }
        }

        private async Task<Dictionary<int, CachedPullRequest>> GetMergedPullRequestsBetween2Refs(string owner, string repository, string fromRef, string toRef)
        {
            IEnumerable<GitHubCommit> commits = null;

            // Get commits for the from/to range
            var response = await this.GitHubClient.Repository.Commit.Compare(owner, repository, fromRef, toRef);
            commits = response.Commits;

            // First load the Issues (PullRequests) for the date range of our commits
            var from = commits.Min(x => x.Commit.Committer.Date).UtcDateTime;
            var to = commits.Max(x => x.Commit.Committer.Date).UtcDateTime;
            var searchRequest = new SearchIssuesRequest
            {
                Merged = new DateRange(from, to),
                Type = IssueTypeQualifier.PullRequest,
                Repos = new RepositoryCollection { string.Concat(owner, "/", repository) },
                PerPage = 1000
            };
            var searchResults = await this.GitHubClient.Search.SearchIssues(searchRequest);

            // Then load the merge events for each PullRequest in parallel
            var eventsTasks = searchResults.Items.Select(issue =>
            {
                return Task.Run(async () =>
                {
                    var events = await this.GitHubClient.Issue.Events.GetAllForIssue(owner, repository, issue.Number);
                    return new KeyValuePair<int, EventInfo>(issue.Number, events.FirstOrDefault(x => x.Event == EventInfoState.Merged));
                });
            });
            var mergeEvents = await Task.WhenAll(eventsTasks);

            // Some of these PRs were in our time range but not for our actual commit range, so  determine which Pull Requests we actually care about (their merge commit is in our commit list)
            var mergedPullRequests = searchResults.Items.Where(x => mergeEvents.Any(y => x.Number == y.Key && commits.Any(z => z.Sha == y.Value.CommitId)));

            // Now load details about the PullRequests using parallel async tasks
            var tasks = mergedPullRequests.Select(pull =>
            {
                return Task.Run(async () =>
                {
                    var pullRequestTask = this.GitHubClient.PullRequest.Get(owner, repository, pull.Number);
                    var issueTask = this.GitHubClient.Issue.Get(owner, repository, pull.Number);
                    var pullRequestCommitsTask = this.GitHubClient.PullRequest.Commits(owner, repository, pull.Number);
                    var pullRequestCommentsTask = this.GitHubClient.Issue.Comment.GetAllForIssue(owner, repository, pull.Number);

                    await Task.WhenAll(pullRequestTask, issueTask, pullRequestCommitsTask, pullRequestCommentsTask);

                    var pullRequestFullCommitsTask = pullRequestCommitsTask.Result.Select(x => this.GitHubClient.Repository.Commit.Get(owner, repository, x.Sha));

                    var pullRequestFullCommits = await Task.WhenAll(pullRequestFullCommitsTask);
                    var contributorUsers = pullRequestFullCommits
                        .Where(x => x.Author != null)
                        .Select(x => x.Author).DistinctBy(x => x.Login);


                    var cachedPullRequest = new CachedPullRequest()
                    {
                        PullRequest = pullRequestTask.Result,
                        Issue = issueTask.Result,
                        Commits = pullRequestCommitsTask.Result.ToList(),
                        Comments = pullRequestCommentsTask.Result.ToList(),
                        MergeCommitSha = mergeEvents.FirstOrDefault(x => x.Key == pull.Number).Value.CommitId,
                        Contributors = contributorUsers.ToList()
                    };

                    return cachedPullRequest;
                });
            });

            // Collect the results
            var cache = await Task.WhenAll(tasks);

            return cache.ToDictionary(x => x.PullRequest.Number);
        }
    }
}
