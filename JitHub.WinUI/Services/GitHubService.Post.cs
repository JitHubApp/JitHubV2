using JitHub.WinUI.Helpers;
using JitHub.Models.PRConversation;
using Octokit;
using IssueUpdate = Octokit.IssueUpdate;
using MergePullRequest = Octokit.MergePullRequest;
using NewPullRequest = Octokit.NewPullRequest;
using NewRepository = Octokit.NewRepository;
using NewRepositoryFork = Octokit.NewRepositoryFork;
using PullRequestMergeMethod = Octokit.PullRequestMergeMethod;
using PullRequestUpdate = Octokit.PullRequestUpdate;
using RepositoryVisibility = Octokit.RepositoryVisibility;
using SortDirection = Octokit.SortDirection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RestGitHubIssue = JitHub.Models.GitHub.GitHubIssue;

namespace JitHub.Services
{
    public partial class GitHubService
    {
        public async Task<Repository> CreateNewRepo(NewRepository repo)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                JitHub.Models.GitHub.GitHubRepository createdRepository = await _gitHubClientService.CreateRepositoryAsync(
                    token,
                    new JitHub.Models.GitHub.GitHubRepositoryCreateOptions
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

        public async Task<bool> IsRepoStarredByCurrentUser(string owner, string name)
        {
            try
            {
                return await _gitHubClientService.IsRepositoryStarredAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    name);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<Repository> ForkRepo(long repoId, NewRepositoryFork fork)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                JitHub.Models.GitHub.GitHubRepository forkedRepository = await _gitHubClientService.ForkRepositoryAsync(token, owner, name);
                return await GetRepository(forkedRepository.Id);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<Subscription> WatchRepo(long repoId)
        {
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

        public async Task<bool> IsCurrentUserWatchingRepo(long repoId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            return await _gitHubClientService.IsRepositoryWatchedAsync(token, owner, name);
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
            JitHub.Models.GitHub.GitHubPullRequestMergeResult result = await _gitHubClientService.MergePullRequestAsync(
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
            try
            {
                string token = GetAccessTokenOrThrow();
                JitHub.Models.GitHub.GitHubPullRequestReviewComment replyComment = await _gitHubClientService.ReplyToPullRequestReviewCommentAsync(
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
            catch
            {
                throw new Exception($"Failed to reply to review comment repoId: {repo.Id}, pr: {number}, inReplyToId: {inReplyToId}");
            }
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
                    RemoveValues(issue.Labels.Select(currentLabel => currentLabel.Name), [label])));
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
            try
            {
                await _gitHubClientService.DeleteIssueCommentReactionAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    repoName,
                    commentId,
                    reactionId);
            }
            catch (Exception)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteIssueCommentReaction(long repoId, long commentId, long reactionId)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.DeleteIssueCommentReactionAsync(token, owner, name, commentId, reactionId);
            }
            catch (Exception)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteReviewCommentReaction(string owner, string repoName, long commentId, long reactionId)
        {
            try
            {
                await _gitHubClientService.DeletePullRequestReviewCommentReactionAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    repoName,
                    commentId,
                    reactionId);
            }
            catch (Exception)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteReviewCommentReaction(long repoId, long commentId, long reactionId)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.DeletePullRequestReviewCommentReactionAsync(token, owner, name, commentId, reactionId);
            }
            catch (Exception)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteIssueReaction(string owner, string name, int num, long id)
        {
            try
            {
                await _gitHubClientService.DeleteIssueReactionAsync(
                    GetAccessTokenOrThrow(),
                    owner,
                    name,
                    num,
                    id);
            }
            catch (Exception)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task ReactToIssue(long repoId, int number, ReactionType type)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.ReactToIssueAsync(token, owner, name, number, type.ToGitHubReactionContent());
            }
            catch (Exception)
            {
                throw new Exception($"Failed to react to issue {number} in repo: {repoId}");
            }
        }

        public async Task ReactToIssueComment(long repoId, long commentId, ReactionType type)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                await _gitHubClientService.ReactToIssueCommentAsync(token, owner, name, commentId, type.ToGitHubReactionContent());
            }
            catch
            {
                throw new Exception($"Failed to react to issue comment: {commentId} in repo: {repoId}");
            }
        }

        public async Task ReactToReviewComment(long repoId, long reviewCommentId, ReactionType type)
        {
            try
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
            catch
            {
                throw new Exception($"Failed to react to pr review comment: {reviewCommentId} in repo: {repoId}");
            }
        }
    }
}



