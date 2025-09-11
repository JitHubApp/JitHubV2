using JitHub.ViewModels.IssueViewModels;
using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Issue;

public sealed partial class UserIssueList : UserControl
{
    private string _id;

    public UserIssueList(string id)
    {
        this.InitializeComponent();
        Loaded += ViewModel.OnLoad;
        _id = id;
    }
}
