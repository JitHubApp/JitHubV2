using JitHub.Models;
using JitHub.WinUI.ViewModels.CommitViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoCommitDetailPage : Page
{
    public CommitDetailViewModel ViewModel { get; } = new();

    public RepoCommitDetailPage()
    {
        this.InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var commit = e.Parameter as CommandableCommit;
        if (commit != null)
        {
            ViewModel.Init(commit);
            ViewModel.LoadCommand.Execute(null);
        }
    }
}


