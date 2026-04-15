using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.PRConversation;
using JitHub.Utilities.SVG;
using JitHub.WinUI.Helpers;
using Octokit;
using ApiOptions = Octokit.ApiOptions;
using CommitRequest = Octokit.CommitRequest;
using IssueFilter = Octokit.IssueFilter;
using IssueRequest = Octokit.IssueRequest;
using IssueSort = Octokit.IssueSort;
using ItemStateFilter = Octokit.ItemStateFilter;
using PullRequestRequest = Octokit.PullRequestRequest;
using PullRequestSort = Octokit.PullRequestSort;
using RepositoryIssueRequest = Octokit.RepositoryIssueRequest;
using SearchRepositoriesRequest = Octokit.SearchRepositoriesRequest;
using SortDirection = Octokit.SortDirection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RestGitHubActor = JitHub.Models.GitHub.GitHubActor;
using RestGitHubBlob = JitHub.Models.GitHub.GitHubBlob;
using RestGitHubBranch = JitHub.Models.GitHub.GitHubBranch;
using RestGitHubCommit = JitHub.Models.GitHub.GitHubCommit;
using RestGitHubCommitComment = JitHub.Models.GitHub.GitHubCommitComment;
using RestGitHubCommitFile = JitHub.Models.GitHub.GitHubCommitFile;
using RestGitHubCommitStats = JitHub.Models.GitHub.GitHubCommitStats;
using RestGitHubIssue = JitHub.Models.GitHub.GitHubIssue;
using RestGitHubIssueComment = JitHub.Models.GitHub.GitHubIssueComment;
using RestGitHubIssueEvent = JitHub.Models.GitHub.GitHubIssueEvent;
using RestGitHubLabel = JitHub.Models.GitHub.GitHubLabel;
using RestGitHubMilestone = JitHub.Models.GitHub.GitHubMilestone;
using RestGitHubPullRequest = JitHub.Models.GitHub.GitHubPullRequest;
using RestGitHubPullRequestReview = JitHub.Models.GitHub.GitHubPullRequestReview;
using RestGitHubReaction = JitHub.Models.GitHub.GitHubReaction;
using RestGitHubRepository = JitHub.Models.GitHub.GitHubRepository;
using RestGitHubRepositoryContent = JitHub.Models.GitHub.GitHubRepositoryContent;
using RestGitHubReactionSummary = JitHub.Models.GitHub.GitHubReactionSummary;
using RestGitHubReviewComment = JitHub.Models.GitHub.GitHubPullRequestReviewComment;
using RestGitHubUser = JitHub.Models.GitHub.GitHubUser;

namespace JitHub.Services
{
    public partial class GitHubService : IGitHubService, IImageProvider, ISVGRenderer
    {
        private const string _baseUrl = "https://github.com";
        private readonly IGitHubClientService _gitHubClientService;
        private INotificationService _notificationService = null!;
        private MarkdownConfig _markdownConfig;
        private string? _accessToken;

        public INotificationService NotificationService
        {
            get => _notificationService;
            set
            {
                _notificationService = value;
            }
        }

        public GitHubService(IGitHubClientService gitHubClientService, INotificationService notificationService)
        {
            _gitHubClientService = gitHubClientService;
            NotificationService = notificationService;
            _markdownConfig = new MarkdownConfig()
            {
                BaseUrl = _baseUrl,
                ImageProvider = this,
                SVGRenderer = this
            };
        }

        public void SetAccessToken(string? token)
        {
            _accessToken = string.IsNullOrWhiteSpace(token) ? null : token;
        }

        private string GetAccessTokenOrThrow()
        {
            return string.IsNullOrWhiteSpace(_accessToken)
                ? throw new InvalidOperationException("GitHub access token is not available.")
                : _accessToken;
        }

        private async Task<(string Owner, string Name)> GetRepositoryIdentityAsync(string token, long repositoryId)
        {
            RestGitHubRepository repository = await _gitHubClientService.GetRepositoryAsync(token, repositoryId);
            return (repository.Owner.Login, repository.Name);
        }

        private static Reaction AdaptReaction(RestGitHubReaction reaction)
        {
            return new Reaction(
                reaction.Id,
                string.Empty,
                AdaptUser(reaction.User),
                reaction.Content.ToReactionType());
        }

        private static User AdaptUser(RestGitHubActor actor)
        {
            return new User(
                actor.AvatarUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                0,
                string.Empty,
                default,
                default,
                0,
                string.Empty,
                0,
                0,
                null,
                actor.HtmlUrl ?? string.Empty,
                0,
                actor.Id,
                string.Empty,
                actor.Login,
                string.Empty,
                string.Empty,
                0,
                null!,
                0,
                0,
                0,
                string.Empty,
                null!,
                false,
                string.Empty,
                null);
        }

        private static User AdaptUser(RestGitHubUser user)
        {
            return new User(
                user.AvatarUrl ?? string.Empty,
                user.Bio ?? string.Empty,
                user.Blog ?? string.Empty,
                0,
                user.Company ?? string.Empty,
                default,
                default,
                0,
                string.Empty,
                user.Followers,
                user.Following,
                null,
                user.HtmlUrl ?? string.Empty,
                0,
                user.Id,
                user.Location ?? string.Empty,
                user.Login,
                user.Name ?? string.Empty,
                string.Empty,
                0,
                null!,
                0,
                0,
                user.PublicRepos,
                string.Empty,
                null!,
                false,
                string.Empty,
                null);
        }

        private static Collaborator AdaptCollaborator(RestGitHubActor actor)
        {
            return new Collaborator(
                actor.Login,
                actor.Id,
                string.Empty,
                string.Empty,
                string.Empty,
                actor.AvatarUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                actor.HtmlUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                new CollaboratorPermissions(),
                string.Empty);
        }

        private static ReactionSummary AdaptReactionSummary(RestGitHubReactionSummary summary)
        {
            return new ReactionSummary(
                summary.TotalCount,
                summary.PlusOne,
                summary.MinusOne,
                summary.Laugh,
                summary.Confused,
                summary.Heart,
                summary.Hooray,
                summary.Eyes,
                summary.Rocket,
                string.Empty);
        }

        private static AuthorAssociation AdaptAuthorAssociation(string? authorAssociation)
        {
            return Enum.TryParse(authorAssociation, true, out AuthorAssociation parsed)
                ? parsed
                : AuthorAssociation.None;
        }

        private static PullRequestReviewComment AdaptReviewComment(RestGitHubReviewComment comment)
        {
            return new PullRequestReviewComment(
                comment.Url ?? comment.HtmlUrl,
                comment.Id,
                comment.NodeId ?? string.Empty,
                comment.DiffHunk ?? string.Empty,
                comment.Path ?? string.Empty,
                comment.Position,
                comment.OriginalPosition,
                comment.CommitId ?? string.Empty,
                comment.OriginalCommitId ?? string.Empty,
                AdaptUser(comment.User),
                comment.Body,
                comment.CreatedAt,
                comment.UpdatedAt,
                comment.HtmlUrl,
                comment.PullRequestUrl ?? string.Empty,
                AdaptReactionSummary(comment.Reactions),
                comment.InReplyToId,
                comment.PullRequestReviewId,
                AdaptAuthorAssociation(comment.AuthorAssociation));
        }

        private static Label AdaptLabel(RestGitHubLabel label)
        {
            return new Label(
                label.Id,
                string.Empty,
                label.Name,
                string.Empty,
                label.Color ?? string.Empty,
                label.Description ?? string.Empty,
                false);
        }

        private static Milestone AdaptMilestone(RestGitHubMilestone milestone)
        {
            return new Milestone(
                string.Empty,
                string.Empty,
                0,
                milestone.Number,
                string.Empty,
                AdaptItemState(milestone.State),
                milestone.Title,
                milestone.Description ?? string.Empty,
                new User(),
                0,
                0,
                default,
                milestone.DueOn,
                null,
                null);
        }

        private static ItemState AdaptItemState(string? state)
        {
            return string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
                ? ItemState.Closed
                : ItemState.Open;
        }

        private static Branch AdaptBranch(RestGitHubBranch branch)
        {
            return new Branch(
                branch.Name,
                new GitReference(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    branch.Name,
                    string.Empty,
                    new User(),
                    new Repository()),
                false);
        }

        private static User AdaptRepositoryOwner(JitHub.Models.GitHub.GitHubRepositoryOwner owner)
        {
            return new User(
                owner.AvatarUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                0,
                string.Empty,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                0,
                string.Empty,
                0,
                0,
                null,
                owner.HtmlUrl ?? string.Empty,
                0,
                0,
                string.Empty,
                owner.Login,
                owner.Login,
                string.Empty,
                0,
                null,
                0,
                0,
                0,
                owner.HtmlUrl ?? string.Empty,
                null,
                false,
                string.Empty,
                null);
        }

        private static Repository AdaptRepository(RestGitHubRepository repository)
        {
            DateTimeOffset updatedAt = repository.UpdatedAt ?? DateTimeOffset.MinValue;
            return new Repository(
                repository.HtmlUrl,
                repository.HtmlUrl,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                repository.Id,
                string.Empty,
                AdaptRepositoryOwner(repository.Owner),
                repository.Name,
                repository.FullName,
                false,
                repository.Description ?? string.Empty,
                string.Empty,
                repository.Language ?? string.Empty,
                repository.Private,
                repository.Fork,
                repository.ForksCount,
                repository.StargazersCount,
                repository.DefaultBranch,
                repository.OpenIssuesCount,
                null,
                updatedAt,
                updatedAt,
                null,
                null,
                null,
                null,
                false,
                true,
                false,
                false,
                false,
                repository.WatchersCount,
                0,
                null,
                null,
                null,
                false,
                repository.WatchersCount,
                null,
                repository.Private ? RepositoryVisibility.Private : RepositoryVisibility.Public,
                Array.Empty<string>(),
                null,
                null,
                null,
                null);
        }

        private static Blob AdaptBlob(RestGitHubBlob blob)
        {
            return new Blob(
                blob.NodeId ?? string.Empty,
                blob.Content ?? string.Empty,
                AdaptEncodingType(blob.Encoding),
                blob.Sha,
                blob.Size);
        }

        private static EncodingType AdaptEncodingType(string? encoding)
        {
            return string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
                ? EncodingType.Base64
                : EncodingType.Utf8;
        }

        private static byte[] DecodeGitHubContent(string? content, string? encoding)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return Array.Empty<byte>();
            }

            if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                string normalized = content.Replace("\r", string.Empty).Replace("\n", string.Empty);
                return Convert.FromBase64String(normalized);
            }

            return Encoding.UTF8.GetBytes(content);
        }

        private static string BuildRepositorySearchQuery(SearchRepositoriesRequest request)
        {
            StringBuilder queryBuilder = new();

            if (!string.IsNullOrWhiteSpace(request.Term))
            {
                queryBuilder.Append(request.Term.Trim());
            }

            if (!string.IsNullOrWhiteSpace(request.User))
            {
                if (queryBuilder.Length > 0)
                {
                    queryBuilder.Append(' ');
                }

                queryBuilder.Append("user:");
                queryBuilder.Append(request.User.Trim());
            }

            return queryBuilder.ToString();
        }

        private static string AdaptIssueStateFilter(ItemStateFilter state)
        {
            return state switch
            {
                ItemStateFilter.Open => "open",
                ItemStateFilter.Closed => "closed",
                _ => "all"
            };
        }

        private static string AdaptIssueSort(IssueSort sort)
        {
            return sort switch
            {
                IssueSort.Created => "created",
                IssueSort.Comments => "comments",
                _ => "updated"
            };
        }

        private static string AdaptSortDirection(SortDirection direction)
        {
            return direction == SortDirection.Ascending ? "asc" : "desc";
        }

        private static JitHub.Models.GitHub.GitHubIssueQueryOptions AdaptIssueQueryOptions(RepositoryIssueRequest request)
        {
            return new JitHub.Models.GitHub.GitHubIssueQueryOptions
            {
                State = AdaptIssueStateFilter(request.State),
                Sort = AdaptIssueSort(request.SortProperty),
                Direction = AdaptSortDirection(request.SortDirection),
                Since = request.Since,
                Labels = request.Labels.Count == 0 ? null : string.Join(',', request.Labels),
                Milestone = string.IsNullOrWhiteSpace(request.Milestone) ? null : request.Milestone,
                Assignee = string.IsNullOrWhiteSpace(request.Assignee) ? null : request.Assignee,
                Creator = string.IsNullOrWhiteSpace(request.Creator) ? null : request.Creator,
                Mentioned = string.IsNullOrWhiteSpace(request.Mentioned) ? null : request.Mentioned,
                Filter = request.Filter.ToString().ToLowerInvariant()
            };
        }

        private static JitHub.Models.GitHub.GitHubIssueQueryOptions AdaptIssueQueryOptions(IssueRequest request)
        {
            return new JitHub.Models.GitHub.GitHubIssueQueryOptions
            {
                State = AdaptIssueStateFilter(request.State),
                Sort = AdaptIssueSort(request.SortProperty),
                Direction = AdaptSortDirection(request.SortDirection),
                Since = request.Since,
                Labels = request.Labels.Count == 0 ? null : string.Join(',', request.Labels),
                Filter = request.Filter.ToString().ToLowerInvariant()
            };
        }

        private static JitHub.Models.GitHub.GitHubPullRequestQueryOptions AdaptPullRequestQueryOptions(PullRequestRequest request)
        {
            return new JitHub.Models.GitHub.GitHubPullRequestQueryOptions
            {
                State = AdaptIssueStateFilter(request.State),
                Sort = request.SortProperty switch
                {
                    PullRequestSort.Created => "created",
                    PullRequestSort.Popularity => "popularity",
                    PullRequestSort.LongRunning => "long-running",
                    _ => "updated"
                },
                Direction = AdaptSortDirection(request.SortDirection),
                Head = string.IsNullOrWhiteSpace(request.Head) ? null : request.Head,
                Base = string.IsNullOrWhiteSpace(request.Base) ? null : request.Base
            };
        }

        private static Issue AdaptIssue(RestGitHubIssue issue, Repository? repository = null)
        {
            IReadOnlyList<User> assignees = issue.Assignees.Select(AdaptUser).ToList();
            return new Issue(
                issue.HtmlUrl,
                issue.HtmlUrl,
                string.Empty,
                string.Empty,
                issue.Number,
                AdaptItemState(issue.State),
                issue.Title,
                issue.Body ?? string.Empty,
                null,
                AdaptUser(issue.User),
                issue.Labels.Select(AdaptLabel).ToList(),
                assignees.FirstOrDefault(),
                assignees,
                issue.Milestone is null ? null : AdaptMilestone(issue.Milestone),
                issue.Comments,
                issue.PullRequest is null ? null : new PullRequest(issue.Number),
                issue.ClosedAt,
                issue.CreatedAt,
                issue.UpdatedAt,
                issue.Id,
                string.Empty,
                false,
                repository,
                AdaptReactionSummary(issue.Reactions),
                null,
                null);
        }

        private static IssueComment AdaptIssueComment(RestGitHubIssueComment comment)
        {
            return new IssueComment(
                comment.Id,
                comment.NodeId ?? string.Empty,
                comment.Url ?? comment.HtmlUrl,
                comment.HtmlUrl,
                comment.Body,
                comment.CreatedAt,
                comment.UpdatedAt,
                AdaptUser(comment.User),
                AdaptReactionSummary(comment.Reactions),
                AdaptAuthorAssociation(comment.AuthorAssociation));
        }

        private static Author AdaptAuthor(RestGitHubActor? actor)
        {
            RestGitHubActor safeActor = actor ?? new RestGitHubActor();
            return new Author(
                safeActor.Login,
                safeActor.Id,
                string.Empty,
                safeActor.AvatarUrl ?? string.Empty,
                string.Empty,
                safeActor.HtmlUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false);
        }

        private static Committer AdaptCommitter(JitHub.Models.GitHub.GitHubCommitSignature signature)
        {
            return new Committer(
                signature.Name ?? string.Empty,
                signature.Email ?? string.Empty,
                signature.Date ?? DateTimeOffset.MinValue);
        }

        private static Commit AdaptCommit(RestGitHubCommit commit)
        {
            return new Commit(
                commit.NodeId ?? string.Empty,
                commit.HtmlUrl,
                string.Empty,
                string.Empty,
                commit.Sha,
                AdaptAuthor(commit.Author ?? commit.Committer),
                null,
                commit.Commit.Message,
                AdaptCommitter(commit.Commit.Author),
                AdaptCommitter(commit.Commit.Committer),
                new GitReference(),
                Array.Empty<GitReference>(),
                commit.Commit.CommentCount,
                null);
        }

        private static GitHubCommitFile AdaptGitHubCommitFile(RestGitHubCommitFile file)
        {
            return new GitHubCommitFile(
                file.Filename,
                file.Additions,
                file.Deletions,
                file.Changes,
                file.Status ?? string.Empty,
                file.BlobUrl ?? string.Empty,
                file.ContentsUrl ?? string.Empty,
                file.RawUrl ?? string.Empty,
                file.Sha ?? string.Empty,
                file.Patch ?? string.Empty,
                file.PreviousFilename ?? string.Empty);
        }

        private static GitHubCommitStats AdaptGitHubCommitStats(RestGitHubCommitStats stats)
        {
            return new GitHubCommitStats(stats.Additions, stats.Deletions, stats.Total);
        }

        private static GitHubCommit AdaptGitHubCommit(RestGitHubCommit commit)
        {
            return new GitHubCommit(
                commit.NodeId ?? string.Empty,
                commit.HtmlUrl,
                string.Empty,
                string.Empty,
                commit.Sha,
                AdaptUser(commit.Author ?? commit.Committer ?? new RestGitHubActor()),
                null,
                AdaptAuthor(commit.Author ?? commit.Committer),
                string.Empty,
                AdaptCommit(commit),
                AdaptAuthor(commit.Committer ?? commit.Author),
                commit.HtmlUrl,
                commit.Stats is null ? null : AdaptGitHubCommitStats(commit.Stats),
                Array.Empty<GitReference>(),
                commit.Files.Select(AdaptGitHubCommitFile).ToList());
        }

        private static PullRequestCommit AdaptPullRequestCommit(RestGitHubCommit commit)
        {
            return new PullRequestCommit(
                commit.NodeId ?? string.Empty,
                AdaptUser(commit.Author ?? commit.Committer ?? new RestGitHubActor()),
                string.Empty,
                AdaptCommit(commit),
                AdaptUser(commit.Committer ?? commit.Author ?? new RestGitHubActor()),
                commit.HtmlUrl,
                Array.Empty<GitReference>(),
                commit.Sha,
                commit.HtmlUrl);
        }

        private static CommitComment AdaptCommitComment(RestGitHubCommitComment comment)
        {
            return new CommitComment(
                comment.Id,
                comment.NodeId ?? string.Empty,
                comment.Url ?? string.Empty,
                comment.HtmlUrl ?? string.Empty,
                comment.Body ?? string.Empty,
                comment.Path ?? string.Empty,
                comment.Position ?? 0,
                comment.Line,
                comment.CommitId ?? string.Empty,
                AdaptUser(comment.User),
                comment.CreatedAt,
                comment.UpdatedAt,
                AdaptReactionSummary(comment.Reactions));
        }

        private static CompareResult AdaptCompareResult(JitHub.Models.GitHub.GitHubCompareResult compareResult)
        {
            return new CompareResult(
                string.Empty,
                compareResult.HtmlUrl ?? string.Empty,
                compareResult.PermalinkUrl ?? string.Empty,
                compareResult.DiffUrl ?? string.Empty,
                compareResult.PatchUrl ?? string.Empty,
                compareResult.BaseCommit is null ? null : AdaptGitHubCommit(compareResult.BaseCommit),
                compareResult.MergeBaseCommit is null ? null : AdaptGitHubCommit(compareResult.MergeBaseCommit),
                compareResult.Status ?? string.Empty,
                compareResult.AheadBy,
                compareResult.BehindBy,
                compareResult.TotalCommits,
                compareResult.Commits.Select(AdaptGitHubCommit).ToList(),
                compareResult.Files.Select(AdaptGitHubCommitFile).ToList());
        }

        private static string NormalizeGitHubEnumValue(string value)
        {
            return value switch
            {
                "converted_to_draft" => "ConvertToDraft",
                _ => string.Concat(
                    value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                        .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()))
            };
        }

        private static TEnum? ParseGitHubEnum<TEnum>(string? value)
            where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = NormalizeGitHubEnumValue(value);
            return Enum.TryParse<TEnum>(normalized, true, out TEnum parsed)
                ? parsed
                : null;
        }

        private static string ToGitHubQueryValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new(value.Length + 4);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (i > 0 && char.IsUpper(character))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        private static GitReference AdaptPullRequestBranch(JitHub.Models.GitHub.GitHubPullRequestBranch branch)
        {
            return new GitReference(
                string.Empty,
                string.Empty,
                branch.Label,
                branch.Ref,
                string.Empty,
                new User(),
                new Repository());
        }

        private static PullRequest AdaptPullRequest(RestGitHubPullRequest pullRequest)
        {
            return new PullRequest(
                pullRequest.Id,
                string.Empty,
                pullRequest.HtmlUrl,
                pullRequest.HtmlUrl,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                pullRequest.Number,
                AdaptItemState(pullRequest.State),
                pullRequest.Title,
                pullRequest.Body ?? string.Empty,
                pullRequest.CreatedAt,
                pullRequest.UpdatedAt,
                string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase) ? pullRequest.UpdatedAt : null,
                pullRequest.Merged ? pullRequest.UpdatedAt : null,
                AdaptPullRequestBranch(pullRequest.Head),
                AdaptPullRequestBranch(pullRequest.Base),
                AdaptUser(pullRequest.User),
                null,
                Array.Empty<User>(),
                pullRequest.Draft,
                pullRequest.Mergeable,
                ParseGitHubEnum<MergeableState>(pullRequest.MergeableState),
                null,
                string.Empty,
                pullRequest.Comments,
                0,
                0,
                0,
                0,
                null,
                false,
                null,
                pullRequest.RequestedReviewers.Select(AdaptUser).ToList(),
                Array.Empty<Team>(),
                Array.Empty<Label>(),
                null);
        }

        private static PullRequestReview AdaptPullRequestReview(RestGitHubPullRequestReview review)
        {
            return new PullRequestReview(
                review.Id,
                review.NodeId ?? string.Empty,
                review.CommitId ?? string.Empty,
                AdaptUser(review.User),
                review.Body ?? string.Empty,
                review.HtmlUrl,
                review.PullRequestUrl ?? string.Empty,
                ParseGitHubEnum<PullRequestReviewState>(review.State) ?? PullRequestReviewState.Pending,
                AdaptAuthorAssociation(review.AuthorAssociation),
                review.SubmittedAt ?? DateTimeOffset.MinValue);
        }

        private static IssueEvent AdaptIssueEvent(RestGitHubIssueEvent issueEvent)
        {
            EventInfoState? parsedEvent = ParseGitHubEnum<EventInfoState>(issueEvent.Event);
            if (!parsedEvent.HasValue || parsedEvent.Value == EventInfoState.ConvertToDraft)
            {
                throw new NotSupportedException($"Unsupported issue event '{issueEvent.Event}'.");
            }

            return new IssueEvent(
                issueEvent.Id,
                issueEvent.NodeId ?? string.Empty,
                issueEvent.Url ?? string.Empty,
                AdaptUser(issueEvent.Actor),
                issueEvent.Assignee is null ? null : AdaptUser(issueEvent.Assignee),
                issueEvent.Label is null ? null : AdaptLabel(issueEvent.Label),
                parsedEvent.Value,
                issueEvent.CommitId ?? string.Empty,
                issueEvent.CreatedAt,
                null,
                string.Empty,
                issueEvent.Rename is null ? null : new RenameInfo(issueEvent.Rename.From, issueEvent.Rename.To),
                null,
                null,
                issueEvent.RequestedReviewer is null ? null : AdaptUser(issueEvent.RequestedReviewer),
                issueEvent.Assigner is null ? null : AdaptUser(issueEvent.Assigner),
                default,
                null,
                null);
        }

        public async Task<User> GetCurrentUser()
        {
            return AdaptUser(await _gitHubClientService.GetCurrentUserAsync(GetAccessTokenOrThrow()));
        }

        public async Task<Repository> GetRepository(string owner, string name)
        {
            try
            {
                return AdaptRepository(await _gitHubClientService.GetRepositoryAsync(GetAccessTokenOrThrow(), owner, name));
            }
            catch (Exception)
            {
                return null!;
            }
        }

        public async Task<Repository> GetRepository(long id)
        {
            try
            {
                return AdaptRepository(await _gitHubClientService.GetRepositoryAsync(GetAccessTokenOrThrow(), id));
            }
            catch (Exception)
            {
                return null!;
            }
        }

        public async Task<ICollection<ConversationNode>> GetPRConversationNodesAsync(Repository repo, PullRequest pr)
        {
            var nodes = new List<ConversationNode>();
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<RestGitHubPullRequestReview> reviews = [];
            List<RestGitHubReviewComment> reviewSingleComments = [];
            List<RestGitHubCommit> commits = [];
            List<RestGitHubIssueEvent> events = [];
            ICollection<IssueCommentNode> comments = await GetIssueComments(repo, pr.Number);

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubPullRequestReview> page = await _gitHubClientService.GetPullRequestReviewsAsync(
                    token,
                    repo.Owner.Login,
                    repo.Name,
                    pr.Number,
                    pageSize,
                    pageNumber);
                reviews.AddRange(page);

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubReviewComment> page = await _gitHubClientService.GetPullRequestReviewCommentsAsync(
                    token,
                    repo.Owner.Login,
                    repo.Name,
                    pr.Number,
                    pageSize,
                    pageNumber);
                reviewSingleComments.AddRange(page);

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubCommit> page = await _gitHubClientService.GetPullRequestCommitsAsync(
                    token,
                    repo.Owner.Login,
                    repo.Name,
                    pr.Number,
                    pageSize,
                    pageNumber);
                commits.AddRange(page);

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubIssueEvent> page = await _gitHubClientService.GetIssueEventsAsync(
                    token,
                    repo.Owner.Login,
                    repo.Name,
                    pr.Number,
                    pageSize,
                    pageNumber);
                events.AddRange(page);

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            var reviewNodes = reviews.Select(review => new ReviewNode(AdaptPullRequestReview(review), repo, pr.Number)).ToList();
            var singleCommentNodes = reviewSingleComments
                .Select(comment => new ReviewCommentNode(AdaptReviewComment(comment), repo, pr.Number))
                .ToList();
            singleCommentNodes.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            var reviewsDict = new Dictionary<long, ReviewNode>();
            var singleCommentsDict = new Dictionary<long, ReviewCommentNode>();

            foreach (var review in reviewNodes)
            {
                reviewsDict.Add(review.Id, review);
            }

            foreach (var singleComment in singleCommentNodes)
            {
                if (!singleComment.InReplyToId.HasValue)
                {
                    singleCommentsDict.Add(singleComment.Id, singleComment);
                }
                else if (singleCommentsDict.ContainsKey(singleComment.InReplyToId.GetValueOrDefault()))
                {
                    singleCommentsDict[singleComment.InReplyToId.GetValueOrDefault()].Replies.Add(singleComment);
                }
            }

            foreach (var singleComment in singleCommentsDict.Values)
            {
                if (reviewsDict.ContainsKey(singleComment.PullRequestReviewId.GetValueOrDefault()))
                {
                    reviewsDict[singleComment.PullRequestReviewId.GetValueOrDefault()].Comments.Add(singleComment);
                }
            }

            nodes.AddRange(reviewsDict.Values.Where(review => review.Comments.Count != 0));
            //only take the ones with comments because github API sometimes return empty review object

            //nodes.AddRange(singleCommentsDict.Values);
            //no need to add the single comments since all are added the reviews
            //github count single comments as reviews too in API
            nodes.AddRange(comments);
            nodes.AddRange(commits.Select(commit => new CommitNode(AdaptPullRequestCommit(commit), repo, pr.Number)));
            foreach (RestGitHubIssueEvent issueEvent in events)
            {
                try
                {
                    nodes.Add(new EventNode(AdaptIssueEvent(issueEvent), repo, pr.Number));
                }
                catch (Exception)
                {
                    //ignore
                }
            }
            nodes.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            return nodes;
        }

        public async Task<ICollection<PullRequest>> GetPullRequests(string owner, string name, PullRequestRequest requestParam, ApiOptions apiOptions)
        {
            int pageSize = apiOptions?.PageSize ?? 100;
            int startPage = apiOptions?.StartPage ?? 1;
            int pageCount = apiOptions?.PageCount ?? 1;
            pageSize = pageSize > 0 ? pageSize : 100;
            startPage = startPage > 0 ? startPage : 1;
            pageCount = pageCount > 0 ? pageCount : 1;

            string token = GetAccessTokenOrThrow();
            List<PullRequest> pullRequests = [];

            for (int offset = 0; offset < pageCount; offset++)
            {
                int pageNumber = startPage + offset;
                IReadOnlyList<RestGitHubPullRequest> page = await _gitHubClientService.GetPullRequestsAsync(
                    token,
                    owner,
                    name,
                    pageSize,
                    pageNumber,
                    AdaptPullRequestQueryOptions(requestParam));
                pullRequests.AddRange(page.Select(AdaptPullRequest));

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return pullRequests;
        }

        public async Task<PullRequest> GetPullRequest(string owner, string name, int num)
        {
            return AdaptPullRequest(await _gitHubClientService.GetPullRequestAsync(GetAccessTokenOrThrow(), owner, name, num));
        }

        public async Task<ICollection<Issue>> GetFilteredIssues(string owner, string name, RepositoryIssueRequest repoIssueRequest, ApiOptions apiOptions)
        {
            try
            {
                int pageSize = apiOptions?.PageSize ?? 100;
                int startPage = apiOptions?.StartPage ?? 1;
                int pageCount = apiOptions?.PageCount ?? 1;
                pageSize = pageSize > 0 ? pageSize : 100;
                startPage = startPage > 0 ? startPage : 1;
                pageCount = pageCount > 0 ? pageCount : 1;
                string token = GetAccessTokenOrThrow();
                List<Issue> issues = [];

                for (int offset = 0; offset < pageCount; offset++)
                {
                    int pageNumber = startPage + offset;
                    IReadOnlyList<RestGitHubIssue> page = await _gitHubClientService.GetIssuesAsync(
                        token,
                        owner,
                        name,
                        pageSize,
                        pageNumber,
                        AdaptIssueQueryOptions(repoIssueRequest));
                    issues.AddRange(page.Select(issue => AdaptIssue(issue)));

                    if (page.Count < pageSize)
                    {
                        break;
                    }
                }

                return issues;
            }
            catch (Exception e)
            {
                NotificationService.Push(e.Message);
                throw new Exception(e.Message);
            }
        }

        public async Task<Issue> GetIssue(string owner, string name, int number)
        {
            return AdaptIssue(await _gitHubClientService.GetIssueAsync(GetAccessTokenOrThrow(), owner, name, number));
        }
        
        public async Task<Issue> GetIssue(long repositoryId, int number)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repositoryId);
            return AdaptIssue(await _gitHubClientService.GetIssueAsync(token, owner, name, number));
        }

        public async Task<ICollection<RestGitHubIssue>> GetCurrentUserIssues(IssueRequest issueRequest)
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<RestGitHubIssue> issues = [];

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubIssue> page = await _gitHubClientService.GetCurrentUserIssuesAsync(
                    token,
                    pageSize,
                    pageNumber,
                    AdaptIssueQueryOptions(issueRequest));
                issues.AddRange(page);

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return issues;
        }

        public async Task<ICollection<IssueCommentNode>> GetIssueComments(Repository repo, int num)
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<IssueCommentNode> res = [];

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubIssueComment> page = await _gitHubClientService.GetIssueCommentsAsync(
                    token,
                    repo.Owner.Login,
                    repo.Name,
                    num,
                    pageSize,
                    pageNumber);
                res.AddRange(page.Select(comment => new IssueCommentNode(AdaptIssueComment(comment), repo, num)));

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            res.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            return res;
        }

        public async Task<ICollection<Repository>> GetAllRepos()
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<Repository> repositories = [];

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubRepository> page = await _gitHubClientService.GetRepositoriesForCurrentUserAsync(
                    token,
                    pageSize,
                    pageNumber);
                repositories.AddRange(page.Select(AdaptRepository));

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return repositories;
        }

        public async Task<ICollection<Repository>> SearchForRepos(string term)
        {
            IReadOnlyList<RestGitHubRepository> repositories = await _gitHubClientService.SearchRepositoriesAsync(
                GetAccessTokenOrThrow(),
                term,
                100);
            return repositories.Select(AdaptRepository).ToList();
        }

        public async Task<ICollection<Repository>> SearchForRepos(SearchRepositoriesRequest request)
        {
            string query = BuildRepositorySearchQuery(request);
            int pageSize = request.PerPage > 0 ? request.PerPage : 100;
            int pageNumber = request.Page > 0 ? request.Page : 1;
            IReadOnlyList<RestGitHubRepository> repositories = await _gitHubClientService.SearchRepositoriesAsync(
                GetAccessTokenOrThrow(),
                query,
                pageSize,
                pageNumber);
            return repositories.Select(AdaptRepository).ToList();
        }

        public async Task<RepoContentNode> GetFileContent(string owner, string name, string path, string _ref)
        {
            RestGitHubRepositoryContent content = await _gitHubClientService.GetRepositoryContentAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                path,
                _ref);
            return new RepoContentNode(content);
        }

        public async Task<Blob> GetBlocFromGit(string owner, string name, string _ref)
        {
            RestGitHubBlob blob = await _gitHubClientService.GetBlobAsync(GetAccessTokenOrThrow(), owner, name, _ref);
            return AdaptBlob(blob);
        }

        public async Task<ICollection<RepoContentNode>> GetRepoContents(string owner, string name, string path, string _ref)
        {
            IReadOnlyList<RestGitHubRepositoryContent> contents = await _gitHubClientService.GetRepositoryContentsAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                path,
                _ref);
            var nodes = new List<RepoContentNode>();
            var dirs = contents
                .Where(content => string.Equals(content.Type, "dir", StringComparison.OrdinalIgnoreCase))
                .Select(content => new RepoContentNode(content))
                .OrderBy(node => node.Name)
                .ToList();
            var files = contents
                .Where(content => !string.Equals(content.Type, "dir", StringComparison.OrdinalIgnoreCase))
                .Select(content => new RepoContentNode(content))
                .OrderBy(node => node.Name)
                .ToList();
            nodes.AddRange(dirs);
            nodes.AddRange(files);
            
            return nodes;
        }

        public async Task<CompareResult> CompareCommits(string owner, string name, string @base, string head)
        {
            return AdaptCompareResult(await _gitHubClientService.CompareCommitsAsync(GetAccessTokenOrThrow(), owner, name, @base, head));
        }

        public async Task<ICollection<Branch>> GetRepoBranches(string owner, string name)
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<Branch> branches = [];

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubBranch> page = await _gitHubClientService.GetBranchesAsync(
                    token,
                    owner,
                    name,
                    pageSize,
                    pageNumber);
                branches.AddRange(page.Select(AdaptBranch));

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return branches;
        }

        public async Task<Branch> GetBranch(string owner, string name, string branch)
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubBranch> page = await _gitHubClientService.GetBranchesAsync(
                    token,
                    owner,
                    name,
                    pageSize,
                    pageNumber);
                RestGitHubBranch? matchingBranch = page.FirstOrDefault(candidate => string.Equals(candidate.Name, branch, StringComparison.OrdinalIgnoreCase));
                if (matchingBranch is not null)
                {
                    return AdaptBranch(matchingBranch);
                }

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            throw new InvalidOperationException($"Branch '{branch}' was not found.");
        }

        public async Task<ICollection<CommitComment>> GetCommentsFromCommit(string owner, string name, string sha)
        {
            const int pageSize = 100;
            string token = GetAccessTokenOrThrow();
            List<CommitComment> comments = [];

            for (int pageNumber = 1; ; pageNumber++)
            {
                IReadOnlyList<RestGitHubCommitComment> page = await _gitHubClientService.GetCommitCommentsAsync(
                    token,
                    owner,
                    name,
                    sha,
                    pageSize,
                    pageNumber);
                comments.AddRange(page.Select(AdaptCommitComment));

                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return comments;
        }

        public async Task<ICollection<CommitComment>> GetCommentsFromCommits(string owner, string name, IEnumerable<GitHubCommit> commits)
        {
            var res = new List<CommitComment>();
            foreach (var commit in commits)
            {
                res.AddRange(await GetCommentsFromCommit(owner, name, commit.Sha));
            }
            return res;
        }

        public ICollection<Author> GetContributorsFromCompareResult(string owner, string name, CompareResult result)
        {
            var res = new Dictionary<string, Author>();
            foreach (var commit in result.Commits)
            {
                if (commit.Author is null || string.IsNullOrWhiteSpace(commit.Author.Login))
                {
                    continue;
                }

                if (!res.ContainsKey(commit.Author.Login))
                {
                    res.Add(commit.Author.Login, commit.Author);
                }
            }
            return res.Values.ToList();
        }

        public async Task<ICollection<PullRequestCommit>> GetCommitsFromPullRequest(string owner, string name, int number)
        {
            IReadOnlyList<RestGitHubCommit> commits = await _gitHubClientService.GetPullRequestCommitsAsync(GetAccessTokenOrThrow(), owner, name, number);
            return commits.Select(AdaptPullRequestCommit).ToList();
        }

        public async Task<ICollection<GitHubCommit>> GetCommits(string owner, string name, CommitRequest request, ApiOptions options)
        {
            int pageSize = options?.PageSize ?? 100;
            int pageNumber = options?.StartPage ?? 1;
            IReadOnlyList<RestGitHubCommit> commits = await _gitHubClientService.GetCommitsAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                request?.Sha,
                pageSize > 0 ? pageSize : 100,
                pageNumber > 0 ? pageNumber : 1);
            return commits.Select(AdaptGitHubCommit).ToList();
        }

        public async Task<GitHubCommit> GetGitHubCommit(string owner, string name, string sha)
        {
            return AdaptGitHubCommit(await _gitHubClientService.GetCommitAsync(GetAccessTokenOrThrow(), owner, name, sha));
        }

        public int GetContributorsCountFromCompareResult(string owner, string name, CompareResult result)
        {
            var authors = GetContributorsFromCompareResult(owner, name, result);
            return authors.Count;
        }

        public async Task<ICollection<Collaborator>> GetRepositoryContributors(string owner, string name)
        {
            IReadOnlyList<RestGitHubActor> contributors = await _gitHubClientService.GetCollaboratorsAsync(GetAccessTokenOrThrow(), owner, name);
            return contributors.Select(AdaptCollaborator).ToList();
        }

        public async Task<ICollection<Collaborator>> GetRepositoryCollaborators(long repoId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            IReadOnlyList<RestGitHubActor> collaborators = await _gitHubClientService.GetCollaboratorsAsync(token, owner, name);
            return collaborators.Select(AdaptCollaborator).ToList();
        }

        public async Task<bool> IsUserCollaborator(long repoId, string? login)
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                return false;
            }

            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            return await _gitHubClientService.IsCollaboratorAsync(token, owner, name, login);
        }

        public async Task<ICollection<User>> GetIssueAssignees(string owner, string name)
        {
            IReadOnlyList<RestGitHubActor> assignees = await _gitHubClientService.GetAssigneesAsync(GetAccessTokenOrThrow(), owner, name);
            return assignees.Select(AdaptUser).ToList();
        }

        public async Task<ICollection<Label>> GetLabelsFromRepository(string owner, string name)
        {
            IReadOnlyList<RestGitHubLabel> labels = await _gitHubClientService.GetLabelsAsync(GetAccessTokenOrThrow(), owner, name);
            return labels.Select(AdaptLabel).ToList();
        }

        public async Task<ICollection<Label>> GetLabelsFromRepository(long repoId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            IReadOnlyList<RestGitHubLabel> labels = await _gitHubClientService.GetLabelsAsync(token, owner, name);
            return labels.Select(AdaptLabel).ToList();
        }

        public async Task<ICollection<Milestone>> GetMilestonesFromRepository(long repoId)
        {
            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            IReadOnlyList<RestGitHubMilestone> milestones = await _gitHubClientService.GetMilestonesAsync(token, owner, name);
            return milestones.Select(AdaptMilestone).ToList();
        }

        public async Task<ICollection<Reaction>> GetReactions(EmojiHost emojiHost, long repoId, int itemId)
        {
            switch (emojiHost)
            {
                case EmojiHost.Issue:
                    return await GetReactionFromIssueAsync(repoId, itemId);
                case EmojiHost.IssueComment:
                    return await GetReactionFromIssueComment(repoId, itemId);
                case EmojiHost.ReviewComment:
                    return await GetReactionFromReviewComment(repoId, itemId);
                default:
                    throw new ArgumentOutOfRangeException(nameof(emojiHost), emojiHost, null);
            }
        }

        public async Task<ICollection<Reaction>> GetReactionFromIssueAsync(long repoId, int number)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                IReadOnlyList<RestGitHubReaction> reactions = await _gitHubClientService.GetIssueReactionsAsync(token, owner, name, number);
                return reactions.Select(AdaptReaction).ToList();
            }
            catch
            {
                var error = $"Failed to fetch reactions from issue: {number} in repo: {repoId}";
                NotificationService.Push(error);
                throw new Exception(error);
            }
        }

        public async Task<ICollection<Reaction>> GetReactionFromIssueComment(long repoId, long commentId)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                IReadOnlyList<RestGitHubReaction> reactions = await _gitHubClientService.GetIssueCommentReactionsAsync(token, owner, name, commentId);
                return reactions.Select(AdaptReaction).ToList();
            }
            catch
            {
                var error = $"Failed to fetch reactions from comment: {commentId} in repo: {repoId}";
                NotificationService.Push(error);
                throw new Exception(error);
            }
        }

        public async Task<ICollection<Reaction>> GetReactionFromReviewComment(long repoId, long commentId)
        {
            try
            {
                string token = GetAccessTokenOrThrow();
                (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
                IReadOnlyList<RestGitHubReaction> reactions = await _gitHubClientService.GetPullRequestReviewCommentReactionsAsync(token, owner, name, commentId);
                return reactions.Select(AdaptReaction).ToList();
            }
            catch
            {
                var error = $"Failed to fetch reactions from review comment: {commentId} in repo: {repoId}";
                NotificationService.Push(error);
                throw new Exception(error);
            }
        }

        public async Task<Image> GetImage(string url)
        {
            Image image = new Image();
            // Split the URL by slashes and get the segments
            string[] segments = url.Split('/');
            // Check if the URL has at least six segments
            if (segments.Length >= 6)
            {
                // Get the owner name, repository name, branch name and file path from the segments
                string owner = segments[3];
                string repo = segments[4];
                string branch = segments[6];
                string filePath = string.Join('/', segments.Skip(7));

                // Get the file content
                try
                {
                    string token = GetAccessTokenOrThrow();
                    RestGitHubRepositoryContent file = await _gitHubClientService.GetRepositoryContentAsync(token, owner, repo, filePath, branch);
                    byte[] fileBytes = DecodeGitHubContent(file.Content, file.Encoding);

                    if (fileBytes.Length == 0 && !string.IsNullOrWhiteSpace(file.Sha))
                    {
                        RestGitHubBlob blob = await _gitHubClientService.GetBlobAsync(token, owner, repo, file.Sha);
                        fileBytes = DecodeGitHubContent(blob.Content, blob.Encoding);
                    }

                    var isSvg = filePath.EndsWith(".svg");

                    if (isSvg)
                    {
                        string svgString = Encoding.UTF8.GetString(fileBytes);
                        image = await SVGRenderer.SvgToImage(svgString);
                    }
                    else
                    {
                        // Create a BitmapImage for other supported formats
                        BitmapImage bitmap = new BitmapImage();
                        using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                        {
                            // Write the data to the stream
                            await stream.WriteAsync(fileBytes.AsBuffer());
                            stream.Seek(0);

                            // Set the source of the BitmapImage
                            await bitmap.SetSourceAsync(stream);
                        }
                        image.Source = bitmap;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
            else
            {
                // The URL is not a valid GitHub file content URL
                throw new Exception("Invalid GitHub file content URL");
            }

            return image;
        }

        public bool ShouldUseThisProvider(string url)
        {
            return Uri.IsWellFormedUriString(url, UriKind.Absolute) && url.StartsWith(_baseUrl);
        }

        public MarkdownConfig GetMarkdownConfig()
        {
            return _markdownConfig;
        }

        public Task<Image> SvgToImage(string svgString)
        {
            return SVGRenderer.SvgToImage(svgString);
        }
    }
}



