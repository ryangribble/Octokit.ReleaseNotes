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

        public async Task<string> GetReleaseNotes(string owner, string repo, string from, string to, int batchSize)
        {
            var sb = new StringBuilder();

            // Get merged PR's between specified refs
            var mergedPulls = await GetMergedPullRequestsBetween2Refs(owner, repo, from, to, batchSize);

            // Remove "skip-release-notes" PR's
            mergedPulls = mergedPulls
                .Where(x => !x.Value.Labels.Contains("skip-release-notes"))
                .ToDictionary(x => x.Key, x => x.Value);
            
            // Group+Order by MileStone
            var groupByMilestone = mergedPulls.Values
                .GroupBy(x => x.PullRequest.Milestone == null ? "zzzNone" : x.PullRequest.Milestone.Title)
                .OrderBy(x => x.Key.ToUpper());

            foreach (var milestoneGroup in groupByMilestone)
            {
                // Milestone Header
                if (groupByMilestone.Count() > 1)
                {
                    sb.AppendFormat("### Milestone: {0}\r\n\r\n", milestoneGroup.Key.Replace("zzzNone", "None"));
                }

                // Group+Order by Label Category
                foreach (var categoryGroup in milestoneGroup.GroupBy(x => x.Category).OrderBy(x => x.Key))
                {
                    // Category Header
                    sb.AppendFormat("**{0}**\r\n\r\n", FormatLabelCategory(categoryGroup.Key));

                    // Order by PullRequest Merge date
                    var pulls = categoryGroup.OrderBy(x => x.PullRequest.MergedAt.Value);

                    foreach (var pull in pulls)
                    {
                        var contributors = new[] { pull.PullRequest.User }
                            .Select(x => FormatContributor(x))
                            .Concat(
                                pull.Contributors
                                    .Select(x => FormatContributor(x))
                                    .OrderBy(x => x))
                            .Distinct();

                        sb.AppendFormat("- {0} - [#{1}]({2}) via {3}\r\n",
                            FormatPulllRequestDescription(pull),
                            pull.PullRequest.Number,
                            pull.PullRequest.HtmlUrl,
                            string.Join(", ", contributors));
                    }

                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string FormatPulllRequestDescription(CachedPullRequest pull)
        {
            // Use "release_notes" comment if exists, otherwise PR title
            var releaseNotesComment = pull.Comments.LastOrDefault(x => x.Body.ToLower().StartsWith("release_notes:"));
            return releaseNotesComment == null ? pull.PullRequest.Title.Trim() : releaseNotesComment.Body.Substring("release_notes:".Length + 1).Trim();
        }

        public string FormatLabelCategory(LabelCategory category)
        {
            switch (category)
            {
                case LabelCategory.Feature:
                    return "Features/Enhancements";
                case LabelCategory.BugFix:
                    return "Fixes";
                case LabelCategory.Housekeeping:
                    return "Housekeeping";
                case LabelCategory.DocumentationUpdate:
                    return "Documentation Updates";
                case LabelCategory.Unknown:
                default:
                    return "Other";
            }
        }

        private object FormatContributor(object contributor)
        {
            var login = "";
            if (contributor is User)
            {
                var user = contributor as User;
                login = user.Login;
            }
            else if (contributor is Author)
            {
                var author = contributor as Author;
                login = author.Login;
            }
            else
            {
                return contributor.ToString();
            }
            
            return $"@{login}";
        }

        // A class containing  the info about a pull request we need to generate release notes
        private class CachedPullRequest
        {
            // The pull request itself
            public PullRequest PullRequest { get; set; }

            // The issue object
            public Issue Issue { get; set; }

            // The merge commit Sha
            public string MergeCommitSha { get; set; }

            // The commits on the PR
            public IEnumerable<PullRequestCommit> Commits { get; set; }

            // The comments on the PR
            public IEnumerable<IssueComment> Comments { get; set; }

            // The contributors on the PR
            public IEnumerable<Author> Contributors { get; set; }

            // The labels on the PR
            public IEnumerable<string> Labels { get; set; }

            // The category of the PR
            public LabelCategory Category { get { return IsFeature() ? LabelCategory.Feature : IsBugFix() ? LabelCategory.BugFix : IsHousekeeping() ? LabelCategory.Housekeeping : IsDoc() ? LabelCategory.DocumentationUpdate : LabelCategory.Unknown; } }

            public bool IsFeature()
            {
                return Labels.Any(x => x.ToLower() == "category: feature");
            }

            public bool IsBugFix()
            {
                return Labels.Any(x => x.ToLower() == "category: bug");
            }

            public bool IsHousekeeping()
            {
                return Labels.Any(x => x.ToLower() == "category: housekeeping");
            }

            public bool IsDoc()
            {
                return Labels.Any(x => x.ToLower() == "category: docs-and-samples");
            }
        }

        public enum LabelCategory
        {
            Feature = 0,
            BugFix = 1,
            Housekeeping = 2,
            DocumentationUpdate = 3,
            Unknown = 4
        }

        private async Task<Dictionary<int, CachedPullRequest>> GetMergedPullRequestsBetween2Refs(string owner, string repository, string fromRef, string toRef, int batchSize)
        {
            IEnumerable<GitHubCommit> commits = null;

            // Get commits for the from/to range
            Console.WriteLine("Get commits in specified range");
            var response = await this.GitHubClient.Repository.Commit.Compare(owner, repository, fromRef, toRef);
            commits = response.Commits;

            // First load the Issues (PullRequests) for the date range of our commits
            var from = commits.Min(x => x.Commit.Committer.Date).UtcDateTime;
            var to = commits.Max(x => x.Commit.Committer.Date).UtcDateTime;
            Console.WriteLine($"Find Pull Requests merged in this date range {from.ToLocalTime()} - {to.ToLocalTime()}");
            var searchRequest = new SearchIssuesRequest
            {
                Merged = new DateRange(from, to),
                Type = IssueTypeQualifier.PullRequest,
                Repos = new RepositoryCollection { string.Concat(owner, "/", repository) },
                PerPage = 1000
            };
            var searchResults = await this.GitHubClient.Search.SearchIssues(searchRequest);
            Console.WriteLine($"Found {searchResults.Items.Count} Pull Requests merged in this date range");

            // Then load the merge events for each PullRequest in parallel
            Console.WriteLine("Get merge events for each Pull Request");
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
            Console.WriteLine("Exclude Pull Requests whose merge commit isn't in our commit range");
            var mergedPullRequests = searchResults.Items.Where(x => mergeEvents.Any(y => x.Number == y.Key && commits.Any(z => z.Sha == y.Value.CommitId)));

            Console.WriteLine($"Found {mergedPullRequests.Count()} merged Pull Requests to load");

            // Now load details about the PullRequests using parallel async tasks
            List<CachedPullRequest> cache = new List<CachedPullRequest>();
            for (int i = 0; i < mergedPullRequests.Count(); i += batchSize)
            {
                var batchPullRequests = mergedPullRequests
                    .Skip(i)
                    .Take(batchSize);

                var tasks = batchPullRequests.Select(pull =>
                {
                    return Task.Run(async () =>
                    {
                        // Load the PR, Issue, Commits and Comments
                        var pullRequestTask = this.GitHubClient.PullRequest.Get(owner, repository, pull.Number);
                        var issueTask = this.GitHubClient.Issue.Get(owner, repository, pull.Number);
                        var pullRequestCommitsTask = this.GitHubClient.PullRequest.Commits(owner, repository, pull.Number);
                        var pullRequestCommentsTask = this.GitHubClient.Issue.Comment.GetAllForIssue(owner, repository, pull.Number);

                        await Task.WhenAll(pullRequestTask, issueTask, pullRequestCommitsTask, pullRequestCommentsTask);

                        // Need to load commits individually to get the author details
                        var pullRequestFullCommitsTask = pullRequestCommitsTask.Result.Select(x => this.GitHubClient.Repository.Commit.Get(owner, repository, x.Sha));

                        var pullRequestFullCommits = await Task.WhenAll(pullRequestFullCommitsTask);

                        // Extract the distinct users who have commits in the PR
                        var contributorUsers = pullRequestFullCommits
                            .Where(x => x.Author != null)
                            .Select(x => x.Author)
                            .DistinctBy(x => x.Login);

                        // Gather everything into a CachedPullRequest object
                        var cachedPullRequest = new CachedPullRequest()
                        {
                            PullRequest = pullRequestTask.Result,
                            Issue = issueTask.Result,
                            Commits = pullRequestCommitsTask.Result.ToList(),
                            Comments = pullRequestCommentsTask.Result.ToList(),
                            MergeCommitSha = mergeEvents.FirstOrDefault(x => x.Key == pull.Number).Value.CommitId,
                            Contributors = contributorUsers.ToList(),
                            Labels = issueTask.Result.Labels.Select(x => x.Name)
                        };

                        return cachedPullRequest;
                    });
                });

                // Collect the results
                cache.AddRange(await Task.WhenAll(tasks));

                Console.WriteLine($"Loaded {cache.Count} Pull Requests");
            }

            return cache.ToDictionary(x => x.PullRequest.Number);
        }
    }
}
