using JitHub.WinUI.Helpers;
using JitHub.Core.Services.GitHub.Transport;
using JitHub.Core.Services.Repositories;
using JitHub.WinUI.Models.LegacyGitHub;
using JitHub.WinUI.Models.PullRequests.Conversation;
using IssueUpdate = JitHub.WinUI.Models.LegacyGitHub.IssueUpdate;
using MergePullRequest = JitHub.WinUI.Models.LegacyGitHub.MergePullRequest;
using NewPullRequest = JitHub.WinUI.Models.LegacyGitHub.NewPullRequest;
using NewRepository = JitHub.WinUI.Models.LegacyGitHub.NewRepository;
using NewRepositoryFork = JitHub.WinUI.Models.LegacyGitHub.NewRepositoryFork;
using PullRequestMergeMethod = JitHub.WinUI.Models.LegacyGitHub.PullRequestMergeMethod;
using PullRequestUpdate = JitHub.WinUI.Models.LegacyGitHub.PullRequestUpdate;
using RepositoryVisibility = JitHub.WinUI.Models.LegacyGitHub.RepositoryVisibility;
using SortDirection = JitHub.WinUI.Models.LegacyGitHub.SortDirection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RestGitHubIssue = JitHub.Core.Models.GitHub.Issues.GitHubIssue;

namespace JitHub.WinUI.Services.GitHub
{
    public partial class GitHubService
    {
        public async Task<Repository> CreateNewRepo(NewRepository repo)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                JitHub.Core.Models.GitHub.Repositories.GitHubRepository createdRepository = await _gitHubClientService.CreateRepositoryAsync(
                    token,
                    new JitHub.Core.Models.GitHub.Repositories.GitHubRepositoryCreateOptions
                    {
                        Name = repo.Name,
                        Description = string.IsNullOrWhiteSpace(repo.Description) ? null : repo.Description,
                        Homepage = string.IsNullOrWhiteSpace(repo.Homepage) ? null : repo.Homepage,
                        Private = repo.Private ?? false,
                        Visibility = (repo.Visibility ?? RepositoryVisibility.Public).ToString().ToLowerInvariant(),
                        AutoInit = repo.AutoInit ?? false,
                        LicenseTemplate = string.IsNullOrWhiteSpace(repo.LicenseTemplate) ? null : repo.LicenseTemplate,
                        GitignoreTemplate = string.IsNullOrWhiteSpace(repo.GitignoreTemplate) ? null : repo.GitignoreTemplate,
                        HasIssues = repo.HasIssues,
                        HasProjects = repo.HasProjects,
                        HasWiki = repo.HasWiki
                    });
                return await GetRepository(createdRepository.Id);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task DeleteRepo(long repoId)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.DeleteRepositoryAsync(token, owner, name);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> StarRepo(string owner, string name)
        {
            if (RepositoryActionAutomationScenario.IsEnabled)
            {
                if (RepositoryActionAutomationScenario.Name.EndsWith("star-failure", StringComparison.Ordinal))
                {
                    throw new GitHubApiException(
                        System.Net.HttpStatusCode.ServiceUnavailable,
                        "Automation star failure.");
                }

                RepositoryActionAutomationScenario.IsStarred = true;
                return true;
            }

            try
            {
                await _gitHubClientService.StarRepositoryAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    name);
                return true;
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> UnstarRepo(string owner, string name)
        {
            if (RepositoryActionAutomationScenario.IsEnabled)
            {
                if (RepositoryActionAutomationScenario.Name.EndsWith("star-failure", StringComparison.Ordinal))
                {
                    throw new GitHubApiException(
                        System.Net.HttpStatusCode.ServiceUnavailable,
                        "Automation unstar failure.");
                }

                RepositoryActionAutomationScenario.IsStarred = false;
                return true;
            }

            try
            {
                await _gitHubClientService.UnstarRepositoryAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    name);
                return true;
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> IsRepoStarredByCurrentUser(
            string owner,
            string name,
            CancellationToken cancellationToken = default)
        {
            if (IsPublicPreviewRepository(owner, name))
            {
                return false;
            }

            try
            {
                return await _gitHubClientService.IsRepositoryStarredAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    name,
                    cancellationToken);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<Repository> ForkRepo(
            long repoId,
            NewRepositoryFork fork,
            CancellationToken cancellationToken = default)
        {
            if (RepositoryActionAutomationScenario.IsEnabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RepositoryActionAutomationScenario.RecordForkPost();
                if (RepositoryActionAutomationScenario.Name.EndsWith("failure", StringComparison.Ordinal))
                {
                    throw new GitHubApiException(
                        System.Net.HttpStatusCode.ServiceUnavailable,
                        "Automation fork failure.");
                }

                string repositoryName = JitHub.WinUI.Program.CurrentLaunchOptions.RepositoryFullName
                    .Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault() ?? "JitHubV2";
                Repository accepted = AdaptRepository(
                    RepositoryActionAutomationScenario.CreateRepository(
                        "automation-user",
                        repositoryName,
                        9100));
                if (RepositoryActionAutomationScenario.Name.EndsWith("reconcile", StringComparison.Ordinal))
                {
                    throw new OperationCanceledException("Automation transport canceled after GitHub accepted the fork.");
                }

                return accepted;
            }

            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId, cancellationToken);
                JitHub.Core.Models.GitHub.Repositories.GitHubRepository forkedRepository = await _gitHubClientService.ForkRepositoryAsync(
                    token,
                    owner,
                    name,
                    cancellationToken);
                return AdaptRepository(forkedRepository);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<Subscription> WatchRepo(long repoId)
        {
            if (RepositoryActionAutomationScenario.IsEnabled)
            {
                if (RepositoryActionAutomationScenario.Name.EndsWith("watch-failure", StringComparison.Ordinal))
                {
                    throw new GitHubApiException(
                        System.Net.HttpStatusCode.ServiceUnavailable,
                        "Automation watch failure.");
                }

                RepositoryActionAutomationScenario.IsWatching = true;
                return new Subscription(
                    subscribed: true,
                    ignored: false,
                    reason: string.Empty,
                    createdAt: DateTimeOffset.UtcNow,
                    url: string.Empty,
                    repositoryUrl: string.Empty);
            }

            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.WatchRepositoryAsync(token, owner, name);
                return new Subscription(
                    subscribed: true,
                    ignored: false,
                    reason: string.Empty,
                    createdAt: DateTimeOffset.UtcNow,
                    url: string.Empty,
                    repositoryUrl: string.Empty);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> UnwatchRepo(long repoId)
        {
            if (RepositoryActionAutomationScenario.IsEnabled)
            {
                if (RepositoryActionAutomationScenario.Name.EndsWith("watch-failure", StringComparison.Ordinal))
                {
                    throw new GitHubApiException(
                        System.Net.HttpStatusCode.ServiceUnavailable,
                        "Automation unwatch failure.");
                }

                RepositoryActionAutomationScenario.IsWatching = false;
                return true;
            }

            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.UnwatchRepositoryAsync(token, owner, name);
                return true;
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<bool> IsCurrentUserWatchingRepo(
            long repoId,
            CancellationToken cancellationToken = default)
        {
            if (IsPublicPreviewToken() && repoId == PublicPreviewRepositoryId)
            {
                return false;
            }

            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId, cancellationToken);
            return await _gitHubClientService.IsRepositoryWatchedAsync(
                token,
                owner,
                name,
                cancellationToken);
        }

        public async Task PostNewIssue(string owner, string name, string title, string body)
        {
            await _gitHubClientService.CreateIssueAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                title,
                body.NormalizeString());
        }

        public async Task PostNewIssue(long id, string title, string body)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, id);
            await _gitHubClientService.CreateIssueAsync(
                token,
                owner,
                name,
                title,
                body.NormalizeString());
        }

        public async Task SubmitComment(string owner, string name, int number, string text)
        {
            await _gitHubClientService.CreateIssueCommentAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                number,
                text.NormalizeString());
        }

        public async Task CloseIssue(string owner, string name, int number, IssueUpdate issueUpdate)
        {
            await _gitHubClientService.UpdateIssueAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                number,
                issueUpdate.Title,
                issueUpdate.Body?.NormalizeString(),
                ToGitHubIssueState(issueUpdate.State));
        }

        public async Task Updateissue(long repoId, int number, IssueUpdate issueUpdate)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.UpdateIssueAsync(
                token,
                owner,
                name,
                number,
                issueUpdate.Title,
                issueUpdate.Body?.NormalizeString(),
                ToGitHubIssueState(issueUpdate.State));
        }

        public async Task CreatePullRequest(string owner, string name, NewPullRequest newPullRequest)
        {
            await _gitHubClientService.CreatePullRequestAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                newPullRequest.Title,
                newPullRequest.Head,
                newPullRequest.Base,
                newPullRequest.Body?.NormalizeString());
        }

        public async Task CreatePullRequestReviewers(string owner, string name, int num, ICollection<string> users)
        {
            await _gitHubClientService.AddPullRequestReviewersAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                num,
                NormalizeDistinctValues(users));
        }

        public async Task RemovePullRequestReviewers(string owner, string name, int num, ICollection<string> users)
        {
            await _gitHubClientService.RemovePullRequestReviewersAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                num,
                NormalizeDistinctValues(users));
        }

        public async Task<PullRequestMerge> MergePullRequest(long repoId, int number, MergePullRequest request)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            JitHub.Core.Models.GitHub.PullRequests.GitHubPullRequestMergeResult result = await _gitHubClientService.MergePullRequestAsync(
                token,
                owner,
                name,
                number,
                request.MergeMethod?.ToString()?.ToLowerInvariant() ?? "merge",
                request.CommitTitle,
                request.CommitMessage?.NormalizeString());
            return new PullRequestMerge(result.Sha ?? request.Sha ?? string.Empty, result.Merged, result.Message);
        }

        public async Task<PullRequest> UpdatePullRequest(long repoId, int number, PullRequestUpdate prUpdate)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.UpdatePullRequestAsync(
                token,
                owner,
                name,
                number,
                prUpdate.Title,
                prUpdate.Body?.NormalizeString(),
                ToGitHubIssueState(prUpdate.State));
            return new PullRequest(number);
        }
        
        public async Task<ReviewCommentNode> ReplyToReview(Repository repo, int number, string replyText, long inReplyToId)
        {
            string token = GetAccessTokenOrThrow();
            JitHub.Core.Models.GitHub.PullRequests.GitHubPullRequestReviewComment replyComment = await _gitHubClientService.ReplyToPullRequestReviewCommentAsync(
                token,
                repo.Owner.Login,
                repo.Name,
                number,
                inReplyToId,
                replyText.NormalizeString());
            PullRequestReviewComment reply = AdaptReviewComment(replyComment);
            // number is a necessary field
            return new ReviewCommentNode(reply, repo, number);
        }

        public async Task AssignIssue(string owner, string name, int num, ICollection<string> users)
        {
            await UpdateIssueMetadataAsync(
                owner,
                name,
                num,
                issue => (
                    MergeDistinctValues(issue.Assignees.Select(assignee => assignee.Login), users),
                    GetLabelNames(issue)));
        }

        public async Task RemoveAssignees(string owner, string name, int num, ICollection<string> users)
        {
            await UpdateIssueMetadataAsync(
                owner,
                name,
                num,
                issue => (
                    RemoveValues(issue.Assignees.Select(assignee => assignee.Login), users),
                    GetLabelNames(issue)));
        }

        public async Task AddLabelToIssue(string owner, string name, int num, ICollection<string> labels)
        {
            await UpdateIssueMetadataAsync(
                owner,
                name,
                num,
                issue => (
                    GetAssigneeLogins(issue),
                    MergeDistinctValues(issue.Labels.Select(label => label.Name), labels)));
        }

        public async Task RemoveLabelFromIssue(string owner, string name, int num, string label)
        {
            await UpdateIssueMetadataAsync(
                owner,
                name,
                num,
                issue => (
                    GetAssigneeLogins(issue),
                    RemoveValues(issue.Labels.Select(currentLabel => currentLabel.Name), new[] { label })));
        }

        private static string? ToGitHubIssueState(ItemState? state)
        {
            return state switch
            {
                ItemState.Open => "open",
                ItemState.Closed => "closed",
                _ => null
            };
        }

        private async Task UpdateIssueMetadataAsync(
            string owner,
            string name,
            int issueNumber,
            Func<RestGitHubIssue, (IReadOnlyList<string> Assignees, IReadOnlyList<string> Labels)> update)
        {
            string token = GetAccessTokenOrThrow();
            RestGitHubIssue issue = await _gitHubClientService.GetIssueAsync(token, owner, name, issueNumber);
            (IReadOnlyList<string> assignees, IReadOnlyList<string> labels) = update(issue);
            await _gitHubClientService.UpdateIssueMetadataAsync(
                token,
                owner,
                name,
                issueNumber,
                assignees,
                labels,
                issue.Milestone?.Number);
        }

        private static IReadOnlyList<string> GetAssigneeLogins(RestGitHubIssue issue)
        {
            return NormalizeDistinctValues(issue.Assignees.Select(assignee => assignee.Login));
        }

        private static IReadOnlyList<string> GetLabelNames(RestGitHubIssue issue)
        {
            return NormalizeDistinctValues(issue.Labels.Select(label => label.Name));
        }

        private static IReadOnlyList<string> MergeDistinctValues(IEnumerable<string> existing, IEnumerable<string> additions)
        {
            return NormalizeDistinctValues(existing.Concat(additions));
        }

        private static IReadOnlyList<string> RemoveValues(IEnumerable<string> existing, IEnumerable<string> removals)
        {
            HashSet<string> removed = NormalizeDistinctValues(removals).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return NormalizeDistinctValues(existing.Where(value => !removed.Contains(value)));
        }

        private static IReadOnlyList<string> NormalizeDistinctValues(IEnumerable<string> values)
        {
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task DeleteIssueCommentReaction(string owner, string repoName, long commentId, long reactionId)
        {
            await _gitHubClientService.DeleteIssueCommentReactionAsync(
                GetAccessTokenOrThrow(),
                owner,
                repoName,
                commentId,
                reactionId);
        }

        public async Task DeleteIssueCommentReaction(long repoId, long commentId, long reactionId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.DeleteIssueCommentReactionAsync(token, owner, name, commentId, reactionId);
        }

        public async Task DeleteReviewCommentReaction(string owner, string repoName, long commentId, long reactionId)
        {
            await _gitHubClientService.DeletePullRequestReviewCommentReactionAsync(
                GetAccessTokenOrThrow(),
                owner,
                repoName,
                commentId,
                reactionId);
        }

        public async Task DeleteReviewCommentReaction(long repoId, long commentId, long reactionId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.DeletePullRequestReviewCommentReactionAsync(token, owner, name, commentId, reactionId);
        }

        public async Task DeleteIssueReaction(string owner, string name, int num, long id)
        {
            await _gitHubClientService.DeleteIssueReactionAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                num,
                id);
        }

        public async Task ReactToIssue(long repoId, int number, ReactionType type)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.ReactToIssueAsync(token, owner, name, number, type.ToGitHubReactionContent());
        }

        public async Task ReactToIssueComment(long repoId, long commentId, ReactionType type)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.ReactToIssueCommentAsync(token, owner, name, commentId, type.ToGitHubReactionContent());
        }

        public async Task ReactToReviewComment(long repoId, long reviewCommentId, ReactionType type)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            await _gitHubClientService.ReactToPullRequestReviewCommentAsync(
                token,
                owner,
                name,
                reviewCommentId,
                type.ToGitHubReactionContent());
        }
    }
}



