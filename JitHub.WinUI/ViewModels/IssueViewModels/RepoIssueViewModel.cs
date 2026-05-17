using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.Filter;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.Views.Controls.Issue;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.UI.Controls;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.ViewModels.IssueViewModels
{
    public partial class RepoIssueViewModel : RepoViewModel
    {
        #region Fields
        private bool _filterLoading;
        private bool _isEmpty;
        private bool _isIncrementalLoading;
        private readonly ModalService _modalSerivce;
        private readonly ICommandService _commandService;
        private IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>> _issues = null!;
        private RepoSelectableItemModel<Issue>? _selectedIssue;
        #endregion

        #region Properties
        public ICommand NewIssueCommand { get; }
        public bool IsEmpty { get => _isEmpty; set => SetProperty(ref _isEmpty, value); }

        public bool IsIncrementalLoading
        {
            get => _isIncrementalLoading;
            set => SetProperty(ref _isIncrementalLoading, value);
        }
        
        public IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>> Issues
        {
            get => _issues;
            set => SetProperty(ref _issues, value);
        }
        public RepoSelectableItemModel<Issue>? SelectedIssue
        {
            get => _selectedIssue;
            set => SetProperty(ref _selectedIssue, value);
        }
        #endregion

        public RepoIssueViewModel()
        {
            _modalSerivce = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
            _commandService = Ioc.Default.GetService<ICommandService>()
                ?? throw new InvalidOperationException("ICommandService is not registered.");
            NewIssueCommand = new RelayCommand(OpenNewIssueDialog);
            InitializeFilters();
        }

        public async void Init(IssueNavArg arg)
        {
            try
            {
                Repo = await GitHubService.GetRepository(arg.Repo.Id);
                RepoSelectableItemModel<Issue>? selectedIssue = null;
                if (!arg.NoDetail)
                {
                    var issue = await GitHubService.GetIssue(Repo.Id, arg.IssueId);
                    selectedIssue = new RepoSelectableItemModel<Issue>() { Model = issue, Repository = Repo };
                }
                var issueSource = new IssueSource(Repo, RepoIssueRequest);
                SetIncrementalCollection(issueSource, selectedIssue);
                await GetFilterParams();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize repository issues: {ex}");
            }
        }

        private void SetIncrementalCollection(IssueSource issueSource, RepoSelectableItemModel<Issue>? selectedIssue)
        {
            Issues = new IncrementalLoadingCollection<IssueSource, RepoSelectableItemModel<Issue>>(issueSource, _perPage);
            Issues.OnStartLoading += () =>
            {
                if (Issues.Count == 0)
                {
                    Loading = true;
                }
                else
                {
                    IsIncrementalLoading = true;
                }
            };
            Issues.OnEndLoading += () =>
            {
                Loading = false;
                IsIncrementalLoading = false;
                IsEmpty = Issues.Count == 0;
            };
            if (selectedIssue != null)
            {
                Issues.Add(selectedIssue);
                SelectedIssue = selectedIssue;
            }
            else
            {
                _ = Issues.RefreshAsync();
            }
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
            if (e.RemovedItems.FirstOrDefault() is RepoSelectableItemModel<Issue> oldItem)
            {
                oldItem.Selected = false;
            }

            if (e.AddedItems.FirstOrDefault() is RepoSelectableItemModel<Issue> newItem)
            {
                newItem.Selected = true;
            }
        }
    }
}




