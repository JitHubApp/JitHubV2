using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.PRConversation;
using JitHub.WinUI.Helpers;
using JitHub.Models.LegacyGitHub;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using ApiOptions = JitHub.Models.LegacyGitHub.ApiOptions;
using CommitRequest = JitHub.Models.LegacyGitHub.CommitRequest;
using IssueFilter = JitHub.Models.LegacyGitHub.IssueFilter;
using IssueRequest = JitHub.Models.LegacyGitHub.IssueRequest;
using IssueSort = JitHub.Models.LegacyGitHub.IssueSort;
using ItemStateFilter = JitHub.Models.LegacyGitHub.ItemStateFilter;
using PullRequestRequest = JitHub.Models.LegacyGitHub.PullRequestRequest;
using PullRequestSort = JitHub.Models.LegacyGitHub.PullRequestSort;
using RepositoryIssueRequest = JitHub.Models.LegacyGitHub.RepositoryIssueRequest;
using SearchRepositoriesRequest = JitHub.Models.LegacyGitHub.SearchRepositoriesRequest;
using SortDirection = JitHub.Models.LegacyGitHub.SortDirection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    public partial class GitHubService : IGitHubService, IMarkdownImageResolver
    {
        private const long PublicPreviewRepositoryId = 623352671;
        private const long PublicPreviewOwnerId = 170190931;
        private const string PublicPreviewOwner = "JitHubApp";
        private const string PublicPreviewRepositoryName = "JitHubV2";
        private const string PublicPreviewDefaultBranch = "main";
        private readonly IGitHubClientService _gitHubClientService;
        private INotificationService _notificationService = null!;
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

        private bool IsPublicPreviewToken()
        {
            return GitHubClientService.IsPublicAccessToken(_accessToken);
        }

        private bool IsPublicPreviewRepository(string owner, string name)
        {
            return IsPublicPreviewToken() && IsPublicPreviewRepositoryName(owner, name);
        }

        private bool IsPublicPreviewRepository(Repository repository)
        {
            return IsPublicPreviewToken() &&
                repository.Id == PublicPreviewRepositoryId &&
                IsPublicPreviewRepositoryName(repository.Owner.Login, repository.Name);
        }

        private static bool IsPublicPreviewRepositoryName(string owner, string name)
        {
            return string.Equals(owner, PublicPreviewOwner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, PublicPreviewRepositoryName, StringComparison.OrdinalIgnoreCase);
        }

        private static User CreatePublicPreviewOwner()
        {
            return new User
            {
                Id = PublicPreviewOwnerId,
                Login = PublicPreviewOwner,
                Name = "JitHub",
                HtmlUrl = $"https://github.com/{PublicPreviewOwner}",
                AvatarUrl = "https://avatars.githubusercontent.com/u/170190931?v=4"
            };
        }

        private static Repository CreatePublicPreviewRepository()
        {
            return new Repository
            {
                Url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepositoryName}",
                HtmlUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}",
                CloneUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}.git",
                GitUrl = $"git://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}.git",
                SshUrl = $"git@github.com:{PublicPreviewOwner}/{PublicPreviewRepositoryName}.git",
                SvnUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}",
                Id = PublicPreviewRepositoryId,
                NodeId = "R_kgDOJTgCXw",
                Owner = CreatePublicPreviewOwner(),
                Name = PublicPreviewRepositoryName,
                FullName = $"{PublicPreviewOwner}/{PublicPreviewRepositoryName}",
                Description = "GitHub WinUI Client",
                Language = "C#",
                Private = false,
                Fork = false,
                ForksCount = 15,
                StargazersCount = 146,
                DefaultBranch = PublicPreviewDefaultBranch,
                OpenIssuesCount = 8,
                CreatedAt = new DateTimeOffset(2023, 4, 3, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero),
                WatchersCount = 146,
                SubscribersCount = 15,
                Visibility = RepositoryVisibility.Public,
                Topics = ["github", "winui", "windows", "client"]
            };
        }

        private static Branch CreatePublicPreviewBranch()
        {
            return new Branch
            {
                Name = PublicPreviewDefaultBranch,
                Commit = new GitReference
                {
                    Ref = PublicPreviewDefaultBranch,
                    Sha = "preview-main-sha",
                    Repository = CreatePublicPreviewRepository()
                },
                Protected = false
            };
        }

        private static List<Label> CreatePublicPreviewLabels()
        {
            return
            [
                new Label
                {
                    Id = 1,
                    Name = "enhancement",
                    Color = "a2eeef",
                    Description = "New feature or request"
                },
                new Label
                {
                    Id = 2,
                    Name = "website",
                    Color = "7057ff",
                    Description = "Website and marketing updates"
                },
                new Label
                {
                    Id = 3,
                    Name = "dependencies",
                    Color = "0366d6",
                    Description = "Dependency updates"
                }
            ];
        }

        private static IReadOnlyList<PullRequest> CreatePublicPreviewPullRequests()
        {
            List<Label> labels = CreatePublicPreviewLabels();

            return new[]
            {
                CreatePublicPreviewPullRequest(
                    72,
                    "Add website dark mode and unblock Store release",
                    "Refreshes the website screenshots, adds theme-aware image handling, and keeps the Store release page aligned with the WinUI app.",
                    new DateTimeOffset(2026, 5, 4, 13, 20, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 5, 4, 17, 10, 0, TimeSpan.Zero),
                    "website-dark-mode",
                    4,
                    new[] { labels[0], labels[1] }),
                CreatePublicPreviewPullRequest(
                    62,
                    "GitHub: Improve developer experience",
                    "Improves repository navigation, issue composition, and developer-facing GitHub workflows in the desktop client.",
                    new DateTimeOffset(2024, 10, 20, 9, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2024, 10, 22, 15, 45, 0, TimeSpan.Zero),
                    "github-developer-experience",
                    2,
                    new[] { labels[0] }),
                CreatePublicPreviewPullRequest(
                    59,
                    "Bump SkiaSharp from 2.88.3 to 2.88.6 in /JitHub.Utilities.SVG",
                    "Updates SkiaSharp packages used by SVG rendering.",
                    new DateTimeOffset(2023, 9, 21, 16, 5, 0, TimeSpan.Zero),
                    new DateTimeOffset(2023, 9, 22, 11, 35, 0, TimeSpan.Zero),
                    "dependabot/nuget/JitHub.Utilities.SVG/SkiaSharp-2.88.6",
                    1,
                    new[] { labels[2] })
            };
        }

        private static PullRequest CreatePublicPreviewPullRequest(
            int number,
            string title,
            string body,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            string headRef,
            int comments,
            IReadOnlyList<Label> labels)
        {
            Repository repository = CreatePublicPreviewRepository();
            User owner = CreatePublicPreviewOwner();
            return new PullRequest
            {
                Id = 100000 + number,
                Url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pulls/{number}",
                HtmlUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pull/{number}",
                DiffUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pull/{number}.diff",
                PatchUrl = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pull/{number}.patch",
                IssueUrl = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/issues/{number}",
                CommitsUrl = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pulls/{number}/commits",
                ReviewCommentsUrl = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepositoryName}/pulls/{number}/comments",
                Number = number,
                State = ItemState.Open,
                Title = title,
                Body = body,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                Head = new GitReference
                {
                    Label = $"{PublicPreviewOwner}:{headRef}",
                    Ref = headRef,
                    Sha = $"preview-pr-{number}-head",
                    User = owner,
                    Repository = repository
                },
                Base = new GitReference
                {
                    Label = $"{PublicPreviewOwner}:{PublicPreviewDefaultBranch}",
                    Ref = PublicPreviewDefaultBranch,
                    Sha = "preview-main-sha",
                    User = owner,
                    Repository = repository
                },
                User = owner,
                Draft = false,
                Mergeable = true,
                MergeableState = MergeableState.Clean,
                Comments = comments,
                Commits = Math.Max(1, comments),
                Additions = 420 + number,
                Deletions = 120,
                ChangedFiles = 12,
                Labels = labels.ToList(),
                Reactions = new ReactionSummary()
            };
        }

        private static PullRequest CreatePublicPreviewPullRequest(int number)
        {
            return CreatePublicPreviewPullRequests().FirstOrDefault(pullRequest => pullRequest.Number == number)
                ?? CreatePublicPreviewPullRequests()[0];
        }

        private static Issue CreatePublicPreviewIssue(int number)
        {
            PullRequest pullRequest = CreatePublicPreviewPullRequest(number);
            return new Issue
            {
                Url = pullRequest.IssueUrl,
                HtmlUrl = pullRequest.HtmlUrl,
                CommentsUrl = $"{pullRequest.IssueUrl}/comments",
                EventsUrl = $"{pullRequest.IssueUrl}/events",
                Number = pullRequest.Number,
                State = ItemState.Open,
                Title = pullRequest.Title,
                Body = pullRequest.Body,
                User = pullRequest.User,
                Labels = pullRequest.Labels.ToList(),
                Assignees = [],
                Comments = pullRequest.Comments,
                PullRequest = pullRequest,
                CreatedAt = pullRequest.CreatedAt,
                UpdatedAt = pullRequest.UpdatedAt,
                Id = pullRequest.Id,
                NodeId = $"preview-issue-{number}",
                Locked = false,
                Repository = CreatePublicPreviewRepository(),
                Reactions = new ReactionSummary(),
                AuthorAssociation = AuthorAssociation.Member
            };
        }

        private async Task<(string Owner, string Name)> GetRepositoryIdentityAsync(string token, long repositoryId)
        {
            if (GitHubClientService.IsPublicAccessToken(token) && repositoryId == PublicPreviewRepositoryId)
            {
                return (PublicPreviewOwner, PublicPreviewRepositoryName);
            }

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
            List<User> assignees = issue.Assignees.Select(AdaptUser).ToList();
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
                issueEvent.DismissedReview,
                string.Empty,
                issueEvent.Rename is null ? null : new RenameInfo(issueEvent.Rename.From, issueEvent.Rename.To),
                issueEvent.RequestedTeam is null
                    ? null
                    : new Team
                    {
                        Name = issueEvent.RequestedTeam.Name,
                        Slug = issueEvent.RequestedTeam.Slug
                    },
                issueEvent.ReviewRequester is null ? null : AdaptUser(issueEvent.ReviewRequester),
                issueEvent.RequestedReviewer is null ? null : AdaptUser(issueEvent.RequestedReviewer),
                issueEvent.Assigner is null ? null : AdaptUser(issueEvent.Assigner),
                issueEvent.LockReason ?? string.Empty,
                issueEvent.Milestone is null ? null : AdaptMilestone(issueEvent.Milestone),
                null);
        }

        public async Task<User> GetCurrentUser()
        {
            return AdaptUser(await _gitHubClientService.GetCurrentUserAsync(GetAccessTokenOrThrow()));
        }

        public async Task<Repository> GetRepository(string owner, string name)
        {
            if (IsPublicPreviewRepository(owner, name))
            {
                return CreatePublicPreviewRepository();
            }

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
            if (IsPublicPreviewToken() && id == PublicPreviewRepositoryId)
            {
                return CreatePublicPreviewRepository();
            }

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
            if (IsPublicPreviewRepository(repo))
            {
                return [];
            }

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

            if (IsPublicPreviewRepository(owner, name))
            {
                return FilterPublicPreviewPullRequests(requestParam, pageSize, startPage, pageCount);
            }

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

        private static ICollection<PullRequest> FilterPublicPreviewPullRequests(
            PullRequestRequest request,
            int pageSize,
            int startPage,
            int pageCount)
        {
            IEnumerable<PullRequest> query = CreatePublicPreviewPullRequests();

            query = request.State switch
            {
                ItemStateFilter.Open => query.Where(pullRequest => pullRequest.State.Value == ItemState.Open),
                ItemStateFilter.Closed => query.Where(pullRequest => pullRequest.State.Value == ItemState.Closed),
                _ => query
            };

            if (!string.IsNullOrWhiteSpace(request.Head))
            {
                query = query.Where(pullRequest =>
                    string.Equals(pullRequest.Head.Ref, request.Head, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pullRequest.Head.Label, request.Head, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.Base))
            {
                query = query.Where(pullRequest =>
                    string.Equals(pullRequest.Base.Ref, request.Base, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pullRequest.Base.Label, request.Base, StringComparison.OrdinalIgnoreCase));
            }

            query = request.SortProperty switch
            {
                PullRequestSort.Updated => query.OrderBy(pullRequest => pullRequest.UpdatedAt),
                PullRequestSort.Popularity => query.OrderBy(pullRequest => pullRequest.Comments),
                PullRequestSort.LongRunning => query.OrderBy(pullRequest => pullRequest.CreatedAt),
                _ => query.OrderBy(pullRequest => pullRequest.CreatedAt)
            };

            if (request.SortDirection != SortDirection.Ascending)
            {
                query = query.Reverse();
            }

            int skip = Math.Max(0, startPage - 1) * pageSize;
            int take = Math.Max(0, pageSize * pageCount);
            return query.Skip(skip).Take(take).ToList();
        }

        public async Task<PullRequest> GetPullRequest(string owner, string name, int num)
        {
            if (IsPublicPreviewRepository(owner, name))
            {
                return CreatePublicPreviewPullRequest(num);
            }

            return AdaptPullRequest(await _gitHubClientService.GetPullRequestAsync(GetAccessTokenOrThrow(), owner, name, num));
        }

        public async Task<ICollection<Issue>> GetFilteredIssues(string owner, string name, RepositoryIssueRequest repoIssueRequest, ApiOptions apiOptions)
        {
            PagedGitHubItems<Issue> page = await GetFilteredIssuesPage(owner, name, repoIssueRequest, apiOptions);
            return page.Items.ToList();
        }

        public async Task<PagedGitHubItems<Issue>> GetFilteredIssuesPage(
            string owner,
            string name,
            RepositoryIssueRequest repoIssueRequest,
            ApiOptions apiOptions)
        {
            try
            {
                int pageSize = apiOptions?.PageSize ?? 100;
                int startPage = apiOptions?.StartPage ?? 1;
                int pageCount = apiOptions?.PageCount ?? 1;
                pageSize = pageSize > 0 ? pageSize : 100;
                startPage = startPage > 0 ? startPage : 1;
                pageCount = pageCount > 0 ? pageCount : 1;

                if (IsPublicPreviewRepository(owner, name))
                {
                    List<Issue> previewIssues = FilterPublicPreviewIssues(repoIssueRequest, pageSize, startPage, pageCount).ToList();
                    return new PagedGitHubItems<Issue>(previewIssues, previewIssues.Count >= pageSize * pageCount);
                }

                string token = GetAccessTokenOrThrow();
                List<Issue> issues = [];
                bool hasMoreItems = false;

                for (int offset = 0; offset < pageCount; offset++)
                {
                    int pageNumber = startPage + offset;
                    IReadOnlyList<RestGitHubIssue> page = await _gitHubClientService.GetIssuesAsync(
                        token,
                        owner,
                        name,
                        pageSize,
                        pageNumber,
                        AdaptIssueQueryOptions(repoIssueRequest),
                        includePullRequests: true);
                    issues.AddRange(page
                        .Where(static issue => !issue.IsPullRequest)
                        .Select(issue => AdaptIssue(issue)));

                    hasMoreItems = page.Count >= pageSize;
                    if (!hasMoreItems)
                    {
                        break;
                    }
                }

                return new PagedGitHubItems<Issue>(issues, hasMoreItems);
            }
            catch (Exception e)
            {
                NotificationService.Push(e.Message);
                throw new Exception(e.Message);
            }
        }

        private static ICollection<Issue> FilterPublicPreviewIssues(
            RepositoryIssueRequest request,
            int pageSize,
            int startPage,
            int pageCount)
        {
            IEnumerable<Issue> query = CreatePublicPreviewPullRequests()
                .Select(pullRequest => CreatePublicPreviewIssue(pullRequest.Number));

            query = request.State switch
            {
                ItemStateFilter.Open => query.Where(issue => issue.State.Value == ItemState.Open),
                ItemStateFilter.Closed => query.Where(issue => issue.State.Value == ItemState.Closed),
                _ => query
            };

            if (request.Labels.Count > 0)
            {
                HashSet<string> labels = request.Labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
                query = query.Where(issue => issue.Labels.Any(label => labels.Contains(label.Name)));
            }

            if (request.Since is DateTimeOffset since)
            {
                query = query.Where(issue => issue.UpdatedAt >= since);
            }

            query = request.SortProperty switch
            {
                IssueSort.Comments => query.OrderBy(issue => issue.Comments),
                IssueSort.Updated => query.OrderBy(issue => issue.UpdatedAt),
                _ => query.OrderBy(issue => issue.CreatedAt)
            };

            if (request.SortDirection != SortDirection.Ascending)
            {
                query = query.Reverse();
            }

            int skip = Math.Max(0, startPage - 1) * pageSize;
            int take = Math.Max(0, pageSize * pageCount);
            return query.Skip(skip).Take(take).ToList();
        }

        public async Task<Issue> GetIssue(string owner, string name, int number)
        {
            if (IsPublicPreviewRepository(owner, name))
            {
                return CreatePublicPreviewIssue(number);
            }

            return AdaptIssue(await _gitHubClientService.GetIssueAsync(GetAccessTokenOrThrow(), owner, name, number));
        }
        
        public async Task<Issue> GetIssue(long repositoryId, int number)
        {
            if (IsPublicPreviewToken() && repositoryId == PublicPreviewRepositoryId)
            {
                return CreatePublicPreviewIssue(number);
            }

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

        public async Task<JitHub.Models.CodeViewer.RepoTree> GetRepoTreeAsync(string owner, string name, string refOrSha, CancellationToken ct)
        {
            Models.GitHub.GitHubTree gitTree = await _gitHubClientService.GetTreeAsync(
                GetAccessTokenOrThrow(),
                owner,
                name,
                refOrSha,
                recursive: true,
                ct);
            return BuildRepoTree(gitTree);
        }

        private static JitHub.Models.CodeViewer.RepoTree BuildRepoTree(Models.GitHub.GitHubTree gitTree)
        {
            var root = new JitHub.Models.CodeViewer.RepoTreeNode
            {
                Name = string.Empty,
                Path = string.Empty,
                IsDirectory = true,
                ParentPath = null,
            };

            // index of path -> node for fast lookup while building tree
            var nodeMap = new Dictionary<string, JitHub.Models.CodeViewer.RepoTreeNode>(StringComparer.Ordinal)
            {
                [string.Empty] = root
            };

            if (gitTree.Tree is not null)
            {
                foreach (var entry in gitTree.Tree)
                {
                    if (string.IsNullOrEmpty(entry.Path))
                        continue;

                    EnsurePath(entry.Path, entry, nodeMap);
                }
            }

            // Sort each directory's children: directories first, then files, each group alphabetical
            SortChildren(root);

            return new JitHub.Models.CodeViewer.RepoTree
            {
                Sha = gitTree.Sha,
                Truncated = gitTree.Truncated,
                Root = root,
            };
        }

        private static JitHub.Models.CodeViewer.RepoTreeNode EnsurePath(
            string path,
            Models.GitHub.GitHubTreeEntry? entry,
            Dictionary<string, JitHub.Models.CodeViewer.RepoTreeNode> nodeMap)
        {
            if (nodeMap.TryGetValue(path, out var existing))
                return existing;

            int slashIndex = path.LastIndexOf('/');
            string parentPath = slashIndex < 0 ? string.Empty : path[..slashIndex];
            string name = slashIndex < 0 ? path : path[(slashIndex + 1)..];

            JitHub.Models.CodeViewer.RepoTreeNode parent = EnsurePath(parentPath, null, nodeMap);

            bool isDir = entry is null || string.Equals(entry.Type, "tree", StringComparison.Ordinal);
            var node = new JitHub.Models.CodeViewer.RepoTreeNode
            {
                Name = name,
                Path = path,
                Sha = entry?.Sha,
                Size = entry?.Size,
                IsDirectory = isDir,
                ParentPath = parentPath,
            };

            nodeMap[path] = node;
            ((List<JitHub.Models.CodeViewer.RepoTreeNode>)parent.Children).Add(node);
            return node;
        }

        private static void SortChildren(JitHub.Models.CodeViewer.RepoTreeNode node)
        {
            var list = (List<JitHub.Models.CodeViewer.RepoTreeNode>)node.Children;
            list.Sort(static (a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                    return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var child in list)
            {
                if (child.IsDirectory)
                    SortChildren(child);
            }
        }

        public async Task<CompareResult> CompareCommits(string owner, string name, string @base, string head)
        {
            return AdaptCompareResult(await _gitHubClientService.CompareCommitsAsync(GetAccessTokenOrThrow(), owner, name, @base, head));
        }

        public async Task<ICollection<Branch>> GetRepoBranches(string owner, string name)
        {
            if (IsPublicPreviewRepository(owner, name))
            {
                return [CreatePublicPreviewBranch()];
            }

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
            if (IsPublicPreviewRepository(owner, name) &&
                string.Equals(branch, PublicPreviewDefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                return CreatePublicPreviewBranch();
            }

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
            if (IsPublicPreviewRepository(owner, name))
            {
                return CreatePublicPreviewLabels().ToList();
            }

            IReadOnlyList<RestGitHubLabel> labels = await _gitHubClientService.GetLabelsAsync(GetAccessTokenOrThrow(), owner, name);
            return labels.Select(AdaptLabel).ToList();
        }

        public async Task<ICollection<Label>> GetLabelsFromRepository(long repoId)
        {
            if (IsPublicPreviewToken() && repoId == PublicPreviewRepositoryId)
            {
                return CreatePublicPreviewLabels().ToList();
            }

            string token = GetAccessTokenOrThrow();
            (string owner, string name) = await GetRepositoryIdentityAsync(token, repoId);
            IReadOnlyList<RestGitHubLabel> labels = await _gitHubClientService.GetLabelsAsync(token, owner, name);
            return labels.Select(AdaptLabel).ToList();
        }

        public async Task<ICollection<Milestone>> GetMilestonesFromRepository(long repoId)
        {
            if (IsPublicPreviewToken() && repoId == PublicPreviewRepositoryId)
            {
                return [];
            }

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

        public async ValueTask<MarkdownImageAsset?> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            if (!GitHubMarkdownImageUrlResolver.TryResolve(
                    source,
                    context.BaseUri,
                    context.DocumentPath,
                    out GitHubMarkdownImageReference imageReference))
            {
                return null;
            }

            try
            {
                string token = GetAccessTokenOrThrow();
                RestGitHubRepositoryContent file = await _gitHubClientService.GetRepositoryContentAsync(
                    token,
                    imageReference.Owner,
                    imageReference.Repository,
                    imageReference.Path,
                    imageReference.Ref,
                    cancellationToken);
                byte[] fileBytes = DecodeGitHubContent(file.Content, file.Encoding);

                if (fileBytes.Length == 0 && !string.IsNullOrWhiteSpace(file.Sha))
                {
                    RestGitHubBlob blob = await _gitHubClientService.GetBlobAsync(
                        token,
                        imageReference.Owner,
                        imageReference.Repository,
                        file.Sha,
                        cancellationToken);
                    fileBytes = DecodeGitHubContent(blob.Content, blob.Encoding);
                }

                return fileBytes.Length == 0
                    ? null
                    : new MarkdownImageAsset(
                        fileBytes,
                        GuessImageContentType(imageReference.Path),
                        GitHubMarkdownImageUrlResolver.CreateRawUri(imageReference));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load GitHub markdown image '{source}': {ex.Message}");
                return null;
            }
        }

        private static string? GuessImageContentType(string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            return extension.ToLowerInvariant() switch
            {
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".ico" => "image/x-icon",
                ".tif" or ".tiff" => "image/tiff",
                _ => null,
            };
        }
    }
}



