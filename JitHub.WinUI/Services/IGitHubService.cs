using CommunityToolkit.WinUI.Controls;
using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.PRConversation;
using Octokit;
using ApiOptions = Octokit.ApiOptions;
using CommitRequest = Octokit.CommitRequest;
using GitHubIssueModel = JitHub.Models.GitHub.GitHubIssue;
using IssueRequest = Octokit.IssueRequest;
using IssueUpdate = Octokit.IssueUpdate;
using MergePullRequest = Octokit.MergePullRequest;
using NewPullRequest = Octokit.NewPullRequest;
using NewRepository = Octokit.NewRepository;
using NewRepositoryFork = Octokit.NewRepositoryFork;
using PullRequestRequest = Octokit.PullRequestRequest;
using PullRequestUpdate = Octokit.PullRequestUpdate;
using RepositoryIssueRequest = Octokit.RepositoryIssueRequest;
using SearchRepositoriesRequest = Octokit.SearchRepositoriesRequest;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JitHub.Services
{
    public interface IGitHubService
    {
        void SetAccessToken(string? token);
        Task<User> GetCurrentUser();
        Task<Repository> GetRepository(string owner, string name);
        Task<Repository> GetRepository(long id);
        Task<ICollection<ConversationNode>> GetPRConversationNodesAsync(Repository repo, PullRequest pr);
        Task<ICollection<PullRequest>> GetPullRequests(string owner, string name, PullRequestRequest requestParam, ApiOptions apiOptions);
        Task<PullRequest> GetPullRequest(string owner, string name, int num);
        Task<ICollection<Issue>> GetFilteredIssues(string owner, string name, RepositoryIssueRequest repoIssueRequest, ApiOptions apiOptions);
        Task<Issue> GetIssue(string owner, string name, int number);
        Task<Issue> GetIssue(long repositoryId, int number);
        Task<ICollection<GitHubIssueModel>> GetCurrentUserIssues(IssueRequest issueRequest);
        Task<ICollection<IssueCommentNode>> GetIssueComments(Repository repo, int num);
        Task<ICollection<Repository>> GetAllRepos();
        Task<ICollection<Repository>> SearchForRepos(string term);
        Task<ICollection<Repository>> SearchForRepos(SearchRepositoriesRequest request);
        Task<RepoContentNode> GetFileContent(string owner, string name, string path, string _ref);
        Task<Blob> GetBlocFromGit(string owner, string name, string _ref);
        Task<ICollection<RepoContentNode>> GetRepoContents(string owner, string name, string path, string _ref);
        Task<CompareResult> CompareCommits(string owner, string name, string @base, string head);
        Task<ICollection<Branch>> GetRepoBranches(string owner, string name);
        Task<Branch> GetBranch(string owner, string name, string branch);

        Task<Repository> CreateNewRepo(NewRepository repo);
        Task DeleteRepo(long repoId);
        Task<bool> StarRepo(string owner, string name);
        Task<bool> UnstarRepo(string owner, string name);
        Task<bool> IsRepoStarredByCurrentUser(string owner, string name);
        Task<Repository> ForkRepo(long repoId, NewRepositoryFork fork);
        Task<Subscription> WatchRepo(long repoId);
        Task<bool> UnwatchRepo(long repoId);
        Task<bool> IsCurrentUserWatchingRepo(long repoId);
        Task PostNewIssue(string owner, string name, string title, string body);
        Task PostNewIssue(long id, string title, string body);
        Task SubmitComment(string owner, string name, int number, string text);
        Task CloseIssue(string owner, string name, int number, IssueUpdate issueUpdate);
        Task Updateissue(long repoId, int number, IssueUpdate issueUpdate);
        Task CreatePullRequest(string owner, string name, NewPullRequest newPullRequest);
        Task<PullRequestMerge> MergePullRequest(long repoId, int number, MergePullRequest request);
        Task<PullRequest> UpdatePullRequest(long repoId, int number, PullRequestUpdate prUpdate);
        Task<ReviewCommentNode> ReplyToReview(Repository repo, int number, string replyText, long inReplyToId);
        Task CreatePullRequestReviewers(string owner, string name, int num, ICollection<string> users);
        Task RemovePullRequestReviewers(string owner, string name, int num, ICollection<string> users);
        Task AssignIssue(string owner, string name, int num, ICollection<string> users);
        Task RemoveAssignees(string owner, string name, int num, ICollection<string> users);
        Task AddLabelToIssue(string owner, string name, int num, ICollection<string> labels);
        Task RemoveLabelFromIssue(string owner, string name, int num, string label);
        Task<ICollection<CommitComment>> GetCommentsFromCommit(string owner, string name, string sha);
        Task<ICollection<CommitComment>> GetCommentsFromCommits(string owner, string name, IEnumerable<GitHubCommit> commits);
        ICollection<Author> GetContributorsFromCompareResult(string owner, string name, CompareResult result);
        int GetContributorsCountFromCompareResult(string owner, string name, CompareResult result);
        Task<ICollection<GitHubCommit>> GetCommits(string owner, string name, CommitRequest request, ApiOptions options);
        Task<GitHubCommit> GetGitHubCommit(string owner, string name, string sha);
        Task<ICollection<PullRequestCommit>> GetCommitsFromPullRequest(string owner, string name, int number);
        Task<ICollection<Collaborator>> GetRepositoryContributors(string owner, string name);
        Task<ICollection<Collaborator>> GetRepositoryCollaborators(long repoId);
        Task<bool> IsUserCollaborator(long repoId, string? login);
        Task<ICollection<User>> GetIssueAssignees(string owner, string name);
        Task<ICollection<Label>> GetLabelsFromRepository(string owner, string name);
        Task<ICollection<Label>> GetLabelsFromRepository(long repoId);
        Task<ICollection<Milestone>> GetMilestonesFromRepository(long repoId);
        Task DeleteIssueCommentReaction(string owner, string repoName, long commentId, long reactionId);
        Task DeleteIssueCommentReaction(long repoId, long commentId, long reactionId);
        Task DeleteReviewCommentReaction(string owner, string repoName, long commentId, long reactionId);
        Task DeleteReviewCommentReaction(long repoId, long commentId, long reactionId);
        Task DeleteIssueReaction(string owner, string name, int num, long id);
        Task ReactToIssue(long repoId, int number, ReactionType type);
        Task ReactToIssueComment(long repoId, long commentId, ReactionType type);
        Task ReactToReviewComment(long repoId, long reviewCommentId, ReactionType type);
        Task<ICollection<Reaction>> GetReactions(EmojiHost emojiHost, long repoId, int itemId);
        Task<ICollection<Reaction>> GetReactionFromIssueAsync(long repoId, int number);
        Task<ICollection<Reaction>> GetReactionFromIssueComment(long repoId, long commentId);
        Task<ICollection<Reaction>> GetReactionFromReviewComment(long repoId, long commentId);
        MarkdownConfig GetMarkdownConfig();
    }
}



