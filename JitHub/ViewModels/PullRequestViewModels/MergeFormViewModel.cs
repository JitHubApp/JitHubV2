using JitHub.Services;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace JitHub.ViewModels.PullRequestViewModels
{
    public class MergeFormViewModel : RepoViewModel
    {
        private string _title;
        private string _body;
        private ICollection<PullRequestMergeMethod> _items;
        private PullRequestMergeMethod _selectedItem;
        private PullRequest _pullRequest;
        private ICommand _callback;
        private ModalService _modalService;

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
            _modalService = Ioc.Default.GetService<ModalService>();
        }

        public void Init(Repository repo, PullRequest pullRequest, PullRequestMergeMethod selectedItem, ICommand callback)
        {
            Repo = repo;
            PullRequest = pullRequest;
            SelectedItem = Items.FirstOrDefault((item) => item == selectedItem);
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
                var res = await GitHubService.MergePullRequest(Repo.Id, PullRequest.Number, mergeRequest);
                if (_callback.CanExecute(null))
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
