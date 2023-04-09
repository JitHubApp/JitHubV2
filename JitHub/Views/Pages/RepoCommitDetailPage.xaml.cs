using JitHub.Models;
using JitHub.ViewModels.CommitViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.Views.Pages;

public sealed partial class RepoCommitDetailPage : Page
{
    public CommitDetailViewModel ViewModel { get; private set; }
    public RepoCommitDetailPage()
    {
        this.InitializeComponent();
        ViewModel = new CommitDetailViewModel();
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
