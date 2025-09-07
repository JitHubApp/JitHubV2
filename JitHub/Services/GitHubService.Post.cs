using JitHub.Helpers;
using JitHub.Models.PRConversation;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JitHub.Services
{
    public partial class GitHubService
    {
        public async Task<Repository> CreateNewRepo(NewRepository repo)
        {
            try
            {
                var repository = await GitHubClient.Repository.Create(repo);
                return repository;
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task DeleteRepo(long repoId)
        {
            try
            {
                await GitHubClient.Repository.Delete(repoId);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> StarRepo(string owner, string name)
        {
            try
            {
                return await GitHubClient.Activity.Starring.StarRepo(owner, name);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> UnstarRepo(string owner, string name)
        {
            try
            {
                return await GitHubClient.Activity.Starring.RemoveStarFromRepo(owner, name);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> IsRepoStarredByCurrentUser(string owner, string name)
        {
            try
            {
                return await GitHubClient.Activity.Starring.CheckStarred(owner, name);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<ICollection<User>> GetAllStargazersOfRepo(long repoId)
        {
            try
            {
                var users = await GitHubClient.Activity.Starring.GetAllStargazers(repoId);
                return users.ToList();
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<ICollection<Repository>> GetAllStarredRepoForUser(string login, StarredRequest request)
        {
            // TODO: use api options for paged results
            try
            {
                var repos = await GitHubClient.Activity.Starring.GetAllForUser(login, request);
                return repos.ToList();
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<ICollection<Repository>> GetAllStarredRepoForCurrentUser(StarredRequest request)
        {
            try
            {
                var repos = await GitHubClient.Activity.Starring.GetAllForCurrent(request);
                return repos.ToList();
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<Repository> ForkRepo(long repoId, NewRepositoryFork fork)
        {
            try
            {
                return await GitHubClient.Repository.Forks.Create(repoId, fork);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<Subscription> WatchRepo(long repoId)
        {
            try
            {
                return await GitHubClient.Activity.Watching.WatchRepo(repoId, new NewSubscription() { Subscribed = true, Ignored = false });
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> UnwatchRepo(long repoId)
        {
            try
            {
                return await GitHubClient.Activity.Watching.UnwatchRepo(repoId);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> IsCurrentUserWatchingRepo(long repoId)
        {
            return await GitHubClient.Activity.Watching.CheckWatched(repoId);
        }

        public async Task PostNewIssue(string owner, string name, string title, string body)
        {
            var issue = new Octokit.NewIssue(title) { Body = body.NormalizeString() };
            await GitHubClient.Issue.Create(owner, name, issue);
        }

        public async Task PostNewIssue(long id, string title, string body)
        {
            var issue = new Octokit.NewIssue(title) { Body = body.NormalizeString() };
            await GitHubClient.Issue.Create(id, issue);
        }

        public async Task SubmitComment(string owner, string name, int number, string text)
        {
            await GitHubClient.Issue.Comment.Create(owner, name, number, text.NormalizeString());
        }

        public async Task CloseIssue(string owner, string name, int number, IssueUpdate issueUpdate)
        {
            await GitHubClient.Issue.Update(owner, name, number, issueUpdate);
        }

        public async Task Updateissue(long repoId, int number, IssueUpdate issueUpdate)
        {
            issueUpdate.Body = issueUpdate.Body.NormalizeString();
            await GitHubClient.Issue.Update(repoId, number, issueUpdate);
        }

        public async Task CreatePullRequest(string owner, string name, NewPullRequest newPullRequest)
        {
            newPullRequest.Body = newPullRequest.Body.NormalizeString();
            await GitHubClient.Repository.PullRequest.Create(owner, name, newPullRequest);
        }

        public async Task CreatePullRequestReviewers(string owner, string name, int num, ICollection<string> users)
        {
            await GitHubClient.Repository.PullRequest.ReviewRequest.Create(owner, name, num, PullRequestReviewRequest.ForReviewers(users.ToList()));
        }

        public async Task RemovePullRequestReviewers(string owner, string name, int num, ICollection<string> users)
        {
            await GitHubClient.Repository.PullRequest.ReviewRequest.Delete(owner, name, num, PullRequestReviewRequest.ForReviewers(users.ToList()));
        }

        public async Task<PullRequestMerge> MergePullRequest(long repoId, int number, MergePullRequest request)
        {
            return await GitHubClient.PullRequest.Merge(repoId, number, request);
        }

        public async Task<PullRequest> UpdatePullRequest(long repoId, int number, PullRequestUpdate prUpdate)
        {
            var newPr = await GitHubClient.PullRequest.Update(repoId, number, prUpdate);
            return newPr;
        }
        
        public async Task<ReviewCommentNode> ReplyToReview(Repository repo, int number, string replyText, long inReplyToId)
        {
            try
            {
                PullRequestReviewComment reply = await GitHubClient.PullRequest.ReviewComment.CreateReply(repo.Id, number, new PullRequestReviewCommentReplyCreate(replyText.NormalizeString(), inReplyToId));
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
            await GitHubClient.Issue.Assignee.AddAssignees(owner, name, num, new AssigneesUpdate(users.ToList()));
        }

        public async Task RemoveAssignees(string owner, string name, int num, ICollection<string> users)
        {
            await GitHubClient.Issue.Assignee.RemoveAssignees(owner, name, num, new AssigneesUpdate(users.ToList()));
        }

        public async Task AddLabelToIssue(string owner, string name, int num, ICollection<string> labels)
        {
            await GitHubClient.Issue.Labels.AddToIssue(owner, name, num, labels.ToArray());
        }

        public async Task RemoveLabelFromIssue(string owner, string name, int num, string label)
        {
            await GitHubClient.Issue.Labels.RemoveFromIssue(owner, name, num, label);
        }

        public async Task DeleteIssueCommentReaction(string owner, string repoName, long commentId, long reactionId)
        {
            try
            {
                await GitHubClient.Reaction.IssueComment.Delete(owner, repoName, commentId, reactionId);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteIssueCommentReaction(long repoId, long commentId, long reactionId)
        {
            try
            {
                await GitHubClient.Reaction.IssueComment.Delete(repoId, commentId, reactionId);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteReviewCommentReaction(string owner, string repoName, long commentId, long reactionId)
        {
            try
            {
                await GitHubClient.Reaction.PullRequestReviewComment.Delete(owner, repoName, commentId, reactionId);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteReviewCommentReaction(long repoId, long commentId, long reactionId)
        {
            try
            {
                await GitHubClient.Reaction.PullRequestReviewComment.Delete(repoId, commentId, reactionId);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task DeleteIssueReaction(string owner, string name, int num, long id)
        {
            try
            {
                string url = $"{GitHubClient.Connection.BaseAddress.ToString()}repos/{owner}/{name}/issues/{num}/reactions/{id}";
                await GitHubClient.Connection.Delete(new Uri(url));
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to delete reaction");
            }
        }

        public async Task ReactToIssue(long repoId, int number, ReactionType type)
        {
            try
            {
                await GitHubClient.Reaction.Issue.Create(repoId, number, new NewReaction(type));
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to react to issue {number} in repo: {repoId}");
            }
        }

        public async Task ReactToIssueComment(long repoId, long commentId, ReactionType type)
        {
            try
            {
                await GitHubClient.Reaction.IssueComment.Create(repoId, commentId, new NewReaction(type));
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
                await GitHubClient.Reaction.PullRequestReviewComment.Create(repoId, reviewCommentId, new NewReaction(type));
            }
            catch
            {
                throw new Exception($"Failed to react to pr review comment: {reviewCommentId} in repo: {repoId}");
            }
        }
    }
}
