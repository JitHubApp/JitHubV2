using JitHub.ViewModels.IssueViewModels;
using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Issue;

public sealed partial class UserIssueList : UserControl
{
    public UserIssueListViewModel ViewModel { get; private set; }
    private string _id;

    public UserIssueList(string id)
    {
        this.InitializeComponent();
        ViewModel = new UserIssueListViewModel();
        DataContext = ViewModel;
        Loaded += ViewModel.OnLoad;
        _id = id;
    }
}
