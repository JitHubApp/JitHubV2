using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.PRConversation;
using JitHub.Utilities.SVG;
using Markdig.UWP;
using Octokit;
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

namespace JitHub.Services
{
    public partial class GitHubService : IGitHubService, IImageProvider, ISVGRenderer
    {
        private GitHubClient _githubClient;
        private const string _baseUrl = "https://github.com";
        private INotificationService _notificationService;
        
        public GitHubClient GitHubClient
        {
            get => _githubClient;
            set
            {
                _githubClient = value;
            }
        }

        public INotificationService NotificationService
        {
            get => _notificationService;
            set
            {
                _notificationService = value;
            }
        }

        public GitHubService()
        {
            GitHubClient = new GitHubClient(new ProductHeaderValue("JitHub"));
            NotificationService = Ioc.Default.GetService<INotificationService>();
        }

        public async Task<Repository> GetRepository(string owner, string name)
        {
            try
            {
                var repo = await GitHubClient.Repository.Get(owner, name);
                return repo;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<Repository> GetRepository(long id)
        {
            try
            {
                var repo = await GitHubClient.Repository.Get(id);
                return repo;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<ICollection<ConversationNode>> GetPRConversationNodesAsync(Repository repo, PullRequest pr)
        {
            var nodes = new List<ConversationNode>();
            var reviews = await GitHubClient.PullRequest.Review.GetAll(repo.Id, pr.Number);
            var comments = await GitHubClient.Issue.Comment.GetAllForIssue(repo.Id, pr.Number);
            var reviewSingleComments = await GitHubClient.PullRequest.ReviewComment.GetAll(repo.Id, pr.Number);
            var commits = await GitHubClient.PullRequest.Commits(repo.Id, pr.Number);
            IEnumerable<IssueEvent> events = await GitHubClient.Issue.Events.GetAllForIssue(repo.Id, pr.Number);
            
            var reviewNodes = reviews.Select(review => new ReviewNode(review, repo, pr.Number)).ToList();
            var singleCommentNodes = reviewSingleComments
                .Select(comment => new ReviewCommentNode(comment, repo, pr.Number))
                .ToList();
            singleCommentNodes.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            var reviewsDict = new Dictionary<long, ReviewNode>();
            var singleCommentsDict = new Dictionary<int, ReviewCommentNode>();

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
            nodes.AddRange(comments.Select(reviewComment => new IssueCommentNode(reviewComment, repo, pr.Number)));
            nodes.AddRange(commits.Select(commit => new CommitNode(commit, repo, pr.Number)));
            foreach (var _event in events)
            {
                try
                {
                    // octokit.net doesn't support the event type "convert_to_draft"
                    // tracking: https://github.com/octokit/octokit.net/issues/2480
                    nodes.Add(new EventNode(_event, repo, pr.Number));
                }
                catch (Exception)
                {
                    //ignore
                }
            }
            nodes.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            return nodes;
        }

        public async Task<ICollection<CheckRun>> GetCheckRuns(string owner, string name, string sha, CheckRunRequest checkRunRequest, ApiOptions apiOptions)
        {
            var checks = await GitHubClient.Check.Run.GetAllForReference(owner, name, sha, checkRunRequest, apiOptions);
            return checks.CheckRuns.ToList();
        }

        public async Task<ICollection<PullRequest>> GetPullRequests(string owner, string name, PullRequestRequest requestParam, ApiOptions apiOptions)
        {
            var rps = await GitHubClient.PullRequest.GetAllForRepository(owner, name, requestParam, apiOptions);
            return rps.ToList();
        }

        public async Task<PullRequest> GetPullRequest(string owner, string name, int num)
        {
            var pr = await GitHubClient.PullRequest.Get(owner, name, num);
            return pr;
        }

        public async Task<ICollection<Activity>> GetActivities(string user, ApiOptions options = null)
        {
            var activities = await GitHubClient.Activity.Events.GetAllUserReceived(user, options == null ? ApiOptions.None : options);
            return activities.ToList();
        }

        public async Task<ICollection<Issue>> GetFilteredIssues(string owner, string name, RepositoryIssueRequest repoIssueRequest, ApiOptions apiOptions)
        {
            try
            {
                var issues = await GitHubClient.Issue.GetAllForRepository(owner, name, repoIssueRequest, apiOptions);
                return issues
                    .Where(issue => issue.PullRequest == null)
                    .ToList();
            }
            catch (Exception e)
            {
                NotificationService.Push(e.Message);
                throw new Exception(e.Message);
            }
        }

        public async Task<Issue> GetIssue(string owner, string name, int number)
        {
            return await GitHubClient.Issue.Get(owner, name, number);
        }
        
        public async Task<Issue> GetIssue(long repositoryId, int number)
        {
            return await GitHubClient.Issue.Get(repositoryId, number);
        }

        public async Task<ICollection<IssueCommentNode>> GetIssueComments(Repository repo, int num)
        {
            var comments = await GitHubClient.Issue.Comment.GetAllForIssue(repo.Id, num);
            var res = comments
                .Select(reviewComment => new IssueCommentNode(reviewComment, repo, num))
                .ToList();
            res.Sort((a, b) => DateTimeOffset.Compare(a.CreatedAt, b.CreatedAt));
            return res;
        }

        public async Task<ICollection<Repository>> GetAllRepos()
        {
            var repos = await GitHubClient.Repository.GetAllForCurrent();
            return repos.ToList();
        }

        public async Task<ICollection<Repository>> SearchForRepos(string term)
        {
            var repos = await GitHubClient.Search.SearchRepo(new SearchRepositoriesRequest(term));
            return repos.Items.ToList();
        }

        public async Task<RepoContentNode> GetFileContent(string owner, string name, string path, string _ref)
        {
            var contents = await GitHubClient.Repository.Content.GetAllContentsByRef(owner, name, path, _ref);
            var node = new RepoContentNode(contents.FirstOrDefault());
            return node;
        }

        public async Task<Blob> GetBlocFromGit(string owner, string name, string _ref)
        {
            var raw = await GitHubClient.Git.Blob.Get(owner, name, _ref);
            return raw;
        }

        public async Task<ICollection<RepoContentNode>> GetRepoContents(string owner, string name, string path, string _ref)
        {
            var contents = await GitHubClient.Repository.Content.GetAllContentsByRef(owner, name, path, _ref);
            var nodes = new List<RepoContentNode>();
            var dirs = contents
                .Where(content => content.Type == Octokit.ContentType.Dir)
                .Select(content => new RepoContentNode(content))
                .OrderBy(node => node.Name)
                .ToList();
            var files = contents
                .Where(content => content.Type != Octokit.ContentType.Dir)
                .Select(content => new RepoContentNode(content))
                .OrderBy(node => node.Name)
                .ToList();
            nodes.AddRange(dirs);
            nodes.AddRange(files);
            
            return nodes;
        }

        public async Task<CompareResult> CompareCommits(string owner, string name, string @base, string head)
        {
            return await GitHubClient.Repository.Commit.Compare(owner, name, @base, head);
        }

        public async Task<ICollection<Branch>> GetRepoBranches(string owner, string name)
        {
            var res = await GitHubClient.Repository.Branch.GetAll(owner, name);
            return res.ToList();
        }

        public async Task<Branch> GetBranch(string owner, string name, string branch)
        {
            return await GitHubClient.Repository.Branch.Get(owner, name, branch);
        }

        public async Task<ICollection<CommitComment>> GetCommentsFromCommit(string owner, string name, string sha)
        {
            var res = await GitHubClient.Repository.Comment.GetAllForCommit(owner, name, sha);
            return res.ToList();
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
                if (!res.ContainsKey(commit.Author.Login))
                {
                    res.Add(commit.Author.Login, commit.Author);
                }
            }
            return res.Values.ToList();
        }

        public async Task<ICollection<PullRequestCommit>> GetCommitsFromPullRequest(string owner, string name, int number)
        {
            var res = await _githubClient.PullRequest.Commits(owner, name, number);
            return res.ToList();
        }

        public async Task<ICollection<GitHubCommit>> GetCommits(string owner, string name, CommitRequest request, ApiOptions options)
        {
            var res = await _githubClient.Repository.Commit.GetAll(owner, name, request, options);
            return res.ToList();
        }

        public async Task<GitHubCommit> GetGitHubCommit(string owner, string name, string sha)
        {
            return await _githubClient.Repository.Commit.Get(owner, name, sha);
        }

        public int GetContributorsCountFromCompareResult(string owner, string name, CompareResult result)
        {
            var authors = GetContributorsFromCompareResult(owner, name, result);
            return authors.Count;
        }

        public async Task<ICollection<Collaborator>> GetRepositoryContributors(string owner, string name)
        {
            var contributors = await _githubClient.Repository.Collaborator.GetAll(owner, name);
            return contributors.ToList();
        }

        public async Task<ICollection<User>> GetIssueAssignees(string owner, string name)
        {
            var assignees = await _githubClient.Issue.Assignee.GetAllForRepository(owner, name);
            return assignees.ToList();
        }

        public async Task<ICollection<Label>> GetLabelsFromRepository(string owner, string name)
        {
            var labels = await _githubClient.Issue.Labels.GetAllForRepository(owner, name);
            return labels.ToList();
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
                var reactions = await GitHubClient.Reaction.Issue.GetAll(repoId, number);
                return reactions.ToList();
            }
            catch
            {
                var error = $"Failed to fetch reactions from issue: {number} in repo: {repoId}";
                NotificationService.Push(error);
                throw new Exception(error);
            }
        }

        public async Task<ICollection<Reaction>> GetReactionFromIssueComment(long repoId, int commentId)
        {
            try
            {
                var reactions = await GitHubClient.Reaction.IssueComment.GetAll(repoId, commentId);
                return reactions.ToList();
            }
            catch
            {
                var error = $"Failed to fetch reactions from comment: {commentId} in repo: {repoId}";
                NotificationService.Push(error);
                throw new Exception(error);
            }
        }

        public async Task<ICollection<Reaction>> GetReactionFromReviewComment(long repoId, int commentId)
        {
            try
            {
                var reactions = await GitHubClient.Reaction.PullRequestReviewComment.GetAll(repoId, commentId);
                return reactions.ToList();
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
                    var file = await GitHubClient.Repository.Content.GetRawContentByRef(owner, repo, filePath, branch);
                    var isSvg = filePath.EndsWith(".svg");

                    if (isSvg)
                    {
                        string svgString = Encoding.UTF8.GetString(file);
                        image = await SVGRenderer.SvgToImage(svgString);
                    }
                    else
                    {
                        // Create a BitmapImage for other supported formats
                        BitmapImage bitmap = new BitmapImage();
                        using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                        {
                            // Write the data to the stream
                            await stream.WriteAsync(file.AsBuffer());
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

        public MarkdownConfig GetMarkdownConfig(string markdown)
        {
            return new MarkdownConfig()
            {
                Markdown = markdown,
                BaseUrl = _baseUrl,
                ImageProvider = this,
                SVGRenderer = this,
            };
        }

        public Task<Image> SvgToImage(string svgString)
        {
            return SVGRenderer.SvgToImage(svgString);
        }
    }
}
