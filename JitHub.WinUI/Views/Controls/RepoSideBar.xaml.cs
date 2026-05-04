using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls;

public sealed partial class RepoSideBar : UserControl
{
    private bool _initialized;

    public RepoSideBar()
    {
        this.InitializeComponent();
    }

    private void RepoSideBar_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (ViewModel.LoadCommand.CanExecute(null))
        {
            ViewModel.LoadCommand.Execute(null);
        }
    }
}

