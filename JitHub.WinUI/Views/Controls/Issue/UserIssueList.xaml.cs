using JitHub.WinUI.ViewModels.IssueViewModels;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class UserIssueList : UserControl
{
    private bool _initialized;
    public UserIssueListViewModel ViewModel { get; private set; }
    
    public UserIssueList()
    {
        this.InitializeComponent();
        ViewModel = new UserIssueListViewModel();
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ViewModel.OnLoad(sender, e);
    }
}

