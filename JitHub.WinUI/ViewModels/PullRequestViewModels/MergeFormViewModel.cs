using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.Models.LegacyGitHub;
using MergePullRequest = JitHub.Models.LegacyGitHub.MergePullRequest;
using PullRequestMergeMethod = JitHub.Models.LegacyGitHub.PullRequestMergeMethod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
public partial class MergeFormViewModel : RepoViewModel
    {
        private string _title = string.Empty;
        private string _body = string.Empty;
        private List<PullRequestMergeMethod> _items = [];
        private PullRequestMergeMethod _selectedItem;
        private PullRequest _pullRequest = null!;
        private ICommand? _callback;
        private string _error = string.Empty;
        private ModalSession? _modalSession;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
        public string Body
        {
            get => _body;
            set => SetProperty(ref _body, value);
        }
        public List<PullRequestMergeMethod> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }
        public PullRequestMergeMethod SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
        }
        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }

        public string Error
        {
            get => _error;
            private set => SetProperty(ref _error, value);
        }

        public IAsyncRelayCommand MergeCommand { get; }

        public MarkdownDocumentSource? MarkdownSource => Repo?.Owner?.Login is string owner &&
            !string.IsNullOrWhiteSpace(owner) &&
            !string.IsNullOrWhiteSpace(Repo.Name) && PullRequest is not null
                ? MarkdownDocumentSourceFactory.CreateRepositoryDocument(
                    "pull-request-merge-draft",
                    PullRequest.Id.ToString(),
                    owner,
                    Repo.Name,
                    PullRequest.Base?.Ref ?? Repo.DefaultBranch)
                : null;

        public MergeFormViewModel()
        {
            Items = new List<PullRequestMergeMethod>
            {
                PullRequestMergeMethod.Merge,
                PullRequestMergeMethod.Rebase,
                PullRequestMergeMethod.Squash
            };
            MergeCommand = new AsyncRelayCommand(MergeAsync);
        }

        public void Init(Repository repo, PullRequest pullRequest, PullRequestMergeMethod selectedItem, ICommand callback)
        {
            Repo = repo;
            PullRequest = pullRequest;
            SelectedItem = selectedItem;
            _callback = callback;
        }

        public void AttachModalSession(ModalSession session) => _modalSession = session;

        private async Task MergeAsync()
        {
            if (_modalSession is not { } session || !session.TryBeginMutation())
            {
                return;
            }

            Error = string.Empty;
            bool merged = false;
            try
            {
                var mergeRequest = new MergePullRequest()
                {
                    CommitTitle = Title,
                    CommitMessage = Body,
                    MergeMethod = SelectedItem,
                };
                _ = await GitHubService.MergePullRequest(Repo.Id, PullRequest.Number, mergeRequest);
                merged = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to merge pull request from merge form: {ex}");
                Error = JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                    "pull-request-merge");
            }
            finally
            {
                session.EndMutation();
            }

            if (!merged)
            {
                return;
            }

            _ = session.TryClose();
            try
            {
                if (_callback?.CanExecute(null) == true)
                {
                    _callback.Execute(null);
                }
            }
            catch (Exception refreshException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Pull request merged, but the refresh callback failed: {refreshException}");
            }
        }
    }
}




