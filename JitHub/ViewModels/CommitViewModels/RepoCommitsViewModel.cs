using JitHub.Converters.Activities;
using JitHub.Models;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.WinUI;

namespace JitHub.ViewModels.CommitViewModels;

public class RepoCommitsViewModel : RepoViewModel
{
    private NavigationService _navigationService;
    private ICollection<Branch> _branches;
    private Branch _selectedBranch;
    private IncrementalLoadingCollection<CommitsSource, CommandableCommit> _commits;
    private CommandableCommit _selectedCommit;
    private CommitPageNavArg _navArgs;
    
    public ICollection<Branch> Branches
    {
        get => _branches;
        set => SetProperty(ref _branches, value);
    }
    public Branch SelectedBranch
    {
        get => _selectedBranch;
        set => SetProperty(ref _selectedBranch, value);
    }
    public IncrementalLoadingCollection<CommitsSource, CommandableCommit> Commits
    {
        get => _commits;
        set => SetProperty(ref _commits, value);
    }
    public CommandableCommit SelectedCommit
    {
        get => _selectedCommit;
        set => SetProperty(ref _selectedCommit, value);
    }
    public ICommand LoadCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ViewCodeCommand { get; }

    public RepoCommitsViewModel(CommitPageNavArg args)
    {
        Repo = args.Repo;
        _navArgs = args;
        _navigationService = Ioc.Default.GetService<NavigationService>();
        LoadCommand = new AsyncRelayCommand(Load);
        CopyCommand = new RelayCommand<string>(Copy);
        ViewCodeCommand = new RelayCommand<string>(ViewCode);
    }

    public void SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        try
        {
            var oldItem = e.RemovedItems[0] as CommandableCommit;
            var newItem = e.AddedItems[0] as CommandableCommit;
            if (oldItem != null)
            {
                oldItem.Selected = false;
            }
            if (newItem != null)
            {
                newItem.Selected = true;
                SelectedCommit = newItem;
            }
            if (newItem == null || e.RemovedItems.Count == 0)
            {
                SelectedCommit = null;
            }
        }
        catch (Exception) { }
    }

    public void BranchSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count != 0)
        {
            // don't fetch again if same name
            var add = (Branch)e.AddedItems[0];
            var removed = (Branch)e.RemovedItems[0];
            if (add.Name == removed.Name) return;
        }
        try
        {
            Reload();
        }
        catch (Exception exception)
        {
            
        }
    }

    private async Task<CommandableCommit> GetCommandableCommit(string gitRef)
    {
        var githubCommit = await GitHubService.GetGitHubCommit(Repo.Owner.Login, Repo.Name, _navArgs.GitRef);
        return new CommandableCommit(Repo, CopyCommand, ViewCodeCommand, githubCommit);
    }

    private void Copy(string sha)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(sha);
        Clipboard.SetContent(dataPackage);
    }

    private void ViewCode(string sha)
    {
        var gitRef = RefFullStringToBranchConverter.ConvertFromRefToBranch(sha);
        _navigationService.RepoNagivateTo(typeof(RepoCodePage), CodeViewerNavArg.CreateWithGitRef(Repo, gitRef));
    }

    private void Reload()
    {
        Loading = true;
        var commitsSource = new CommitsSource(Repo, new CommitRequest { Sha = SelectedBranch.Name }, CopyCommand, ViewCodeCommand);
        Commits = new IncrementalLoadingCollection<CommitsSource, CommandableCommit>(commitsSource, 50);
        Commits.RefreshAsync();
        Loading = false;
    }

    public async Task Load()
    {
        Loading = true;
        Repo = await GitHubService.GetRepository(Repo.Id);
        Branches = await GitHubService.GetRepoBranches(Repo.Owner.Login, Repo.Name);
        if (!_navArgs.NoBranch)
        {
            SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == _navArgs.Branch);
        }
        else
        {
            SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == Repo.DefaultBranch);
        }
        try
        {
            Reload();
        }
        catch (Exception exception)
        {

        }
        if (!_navArgs.NoRef)
        {
            if (SelectedCommit == null && Commits != null)
            {
                var commandableCommit = await GetCommandableCommit(_navArgs.GitRef);
                Commits.Add(commandableCommit);
                SelectedCommit = Commits.FirstOrDefault(commit => commit.Sha == _navArgs.GitRef);
            }
        }
        Loading = false;
    }
}
