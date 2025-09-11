using JitHub.ViewModels.IssueViewModels;
using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Issue;

public sealed partial class UserIssueList : UserControl
{
    private string _id;

    public UserIssueList(string id, IssueListType type)
    {
        this.InitializeComponent();
        ViewModel.ListType = type;
        Loaded += ViewModel.OnLoad;
        _id = id;
    }
}
