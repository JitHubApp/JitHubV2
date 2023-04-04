using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.Filter;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Controls.Issue;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Octokit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI.Xaml.Controls;

namespace JitHub.ViewModels.IssueViewModels
{
    public partial class RepoIssueViewModel : RepoViewModel
    {
        #region Fields
        private bool _filterLoading;
        private bool _isEmpty;
        private ModalService _modalSerivce;
        private ICommandService _commandService;
        private IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>> _issues;
        private RepoSelectableItemModel<Issue> _selectedIssue;
        #endregion

        #region Properties
        public ICommand NewIssueCommand { get; }
        public bool IsEmpty { get => _isEmpty; set => SetProperty(ref _isEmpty, value); }
        
        public IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>> Issues
        {
            get => _issues;
            set => SetProperty(ref _issues, value);
        }
        public RepoSelectableItemModel<Issue> SelectedIssue
        {
            get => _selectedIssue;
            set => SetProperty(ref _selectedIssue, value);
        }
        #endregion

        public RepoIssueViewModel()
        {
            _modalSerivce = Ioc.Default.GetService<ModalService>();
            _commandService = Ioc.Default.GetService<ICommandService>();
            NewIssueCommand = new RelayCommand(OpenNewIssueDialog);
            InitializeFilters();
        }

        public async void Init(IssueNavArg arg)
        {
            Repo = await GitHubService.GetRepository(arg.Repo.Id);
            RepoSelectableItemModel<Issue> selectedIssue = null;
            if (!arg.NoDetail)
            {
                var issue = await GitHubService.GetIssue(Repo.Id, arg.IssueId);
                selectedIssue = new RepoSelectableItemModel<Issue>() { Model = issue, Repository = Repo };
            }
            var issueSource = new IssueSource(Repo, RepoIssueRequest);
            SetIncrementalCollection(issueSource, selectedIssue);
            await GetFilterParams();
        }

        private void SetIncrementalCollection(IssueSource issueSource, RepoSelectableItemModel<Issue> selectedIssue)
        {
            Issues = new IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>>(issueSource, _perPage);
            if (selectedIssue != null)
            {
                Issues.Add(selectedIssue);
                SelectedIssue = selectedIssue;
            }
            else
            {
                Issues.RefreshAsync();
            }
            Issues.OnStartLoading += () =>
            {
                Loading = true;
            };
            Issues.OnEndLoading += () =>
            {
                Loading = false;
                IsEmpty = Issues.Count == 0;
            };
        }

        private void OpenNewIssueDialog()
        {
            _modalSerivce.Open("New Issue", new IssueForm(_commandService.GetCommand(JitHubCommand.CreateNewIssue), "", "", null, Repo.Id));
        }

        //TODO: needs to handle this in GroupFilters with editable Dropdown
        public void UserSelectionBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
        {
            var selectedUser = new Selection(sender.Text, sender.Text, null);
            sender.SelectedItem = selectedUser;
            args.Handled = true;
        }

        public void IssuePageMasterDetail_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var oldItem = e.RemovedItems[0] as RepoSelectableItemModel<Issue>;
                var newItem = e.AddedItems[0] as RepoSelectableItemModel<Issue>;
                if (oldItem != null)
                    oldItem.Selected = false;
                if (newItem != null)
                {
                    newItem.Selected = true;
                }
            }
            catch (Exception)
            { }
        }
    }
}
