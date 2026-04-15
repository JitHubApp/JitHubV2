using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using MergePullRequest = Octokit.MergePullRequest;
using PullRequestMergeMethod = Octokit.PullRequestMergeMethod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public class MergeFormViewModel : RepoViewModel
    {
        private string _title = string.Empty;
        private string _body = string.Empty;
        private ICollection<PullRequestMergeMethod> _items = [];
        private PullRequestMergeMethod _selectedItem;
        private PullRequest _pullRequest = null!;
        private ICommand? _callback;
        private readonly ModalService _modalService;

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
        public ICollection<PullRequestMergeMethod> Items
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

        public MergeFormViewModel()
        {
            Items = new List<PullRequestMergeMethod>
            {
                PullRequestMergeMethod.Merge,
                PullRequestMergeMethod.Rebase,
                PullRequestMergeMethod.Squash
            };
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
        }

        public void Init(Repository repo, PullRequest pullRequest, PullRequestMergeMethod selectedItem, ICommand callback)
        {
            Repo = repo;
            PullRequest = pullRequest;
            SelectedItem = selectedItem;
            _callback = callback;
        }

        public async void Merge()
        {
            try
            {
                var mergeRequest = new MergePullRequest()
                {
                    CommitTitle = Title,
                    CommitMessage = Body,
                    MergeMethod = SelectedItem,
                };
                _ = await GitHubService.MergePullRequest(Repo.Id, PullRequest.Number, mergeRequest);
                if (_callback?.CanExecute(null) == true)
                {
                    _callback.Execute(null);
                }
            }
            catch (Exception)
            {

            }
            _modalService.Close();
        }
    }
}




