using JitHub.ViewModels.IssueViewModels;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Issue;

public sealed partial class UserIssueList : UserControl
{
    public UserIssueListViewModel ViewModel { get; private set; }
    
    public UserIssueList()
    {
        this.InitializeComponent();
        ViewModel = new UserIssueListViewModel();
        DataContext = ViewModel;
        Loaded += ViewModel.OnLoad;
    }
}
