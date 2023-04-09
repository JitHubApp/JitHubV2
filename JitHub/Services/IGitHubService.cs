using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.PRConversation;
using Markdig.UWP;
using Octokit;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.Services
{
    public interface IGitHubService
    {
        GitHubClient GitHubClient { get; set; }
        Task<Repository> GetRepository(string owner, string name);
        Task<Repository> GetRepository(long id);
        Task<ICollection<ConversationNode>> GetPRConversationNodesAsync(Repository repo, PullRequest pr);
        Task<ICollection<CheckRun>> GetCheckRuns(string owner, string name, string sha, CheckRunRequest checkRunRequest, ApiOptions apiOptions);
        Task<ICollection<PullRequest>> GetPullRequests(string owner, string name, PullRequestRequest requestParam, ApiOptions apiOptions);
        Task<PullRequest> GetPullRequest(string owner, string name, int num);
        Task<ICollection<Activity>> GetActivities(string user, ApiOptions options);
        Task<ICollection<Issue>> GetFilteredIssues(string owner, string name, RepositoryIssueRequest repoIssueRequest, ApiOptions apiOptions);
        Task<Issue> GetIssue(string owner, string name, int number);
        Task<Issue> GetIssue(long repositoryId, int number);
        Task<ICollection<IssueCommentNode>> GetIssueComments(Repository repo, int num);
        Task<ICollection<Repository>> GetAllRepos();
        Task<ICollection<Repository>> SearchForRepos(string term);
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
        Task<ICollection<User>> GetAllStargazersOfRepo(long repoId);
        Task<ICollection<Repository>> GetAllStarredRepoForUser(string login, StarredRequest request);
        Task<ICollection<Repository>> GetAllStarredRepoForCurrentUser(StarredRequest request);
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
        Task<ReviewCommentNode> ReplyToReview(Repository repo, int number, string replyText, int inReplyToId);
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
        Task<ICollection<User>> GetIssueAssignees(string owner, string name);
        Task<ICollection<Label>> GetLabelsFromRepository(string owner, string name);
        Task DeleteIssueCommentReaction(string owner, string repoName, int commentId, int reactionId);
        Task DeleteIssueCommentReaction(long repoId, int commentId, int reactionId);
        Task DeleteReviewCommentReaction(string owner, string repoName, int commentId, int reactionId);
        Task DeleteReviewCommentReaction(long repoId, int commentId, int reactionId);
        Task DeleteIssueReaction(string owner, string name, int num, int id);
        Task ReactToIssue(long repoId, int number, ReactionType type);
        Task ReactToIssueComment(long repoId, int commentId, ReactionType type);
        Task ReactToReviewComment(long repoId, int reviewCommentId, ReactionType type);
        Task<ICollection<Reaction>> GetReactions(EmojiHost emojiHost, long repoId, int itemId);
        Task<ICollection<Reaction>> GetReactionFromIssueAsync(long repoId, int number);
        Task<ICollection<Reaction>> GetReactionFromIssueComment(long repoId, int commentId);
        Task<ICollection<Reaction>> GetReactionFromReviewComment(long repoId, int commentId);
        MarkdownConfig GetMarkdownConfig(string markdown);
    }
}
