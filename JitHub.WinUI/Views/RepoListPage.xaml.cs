using JitHub.WinUI.ViewModels;
using JitHub.WinUI.ViewModels.RepositoryViewModels;
using Microsoft.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.WinUI.Views
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoListPage : Page
    {
        private bool _initialized;

        public RepoListViewModel ViewModel { get; } = new();

        public RepoListPage()
        {
            this.InitializeComponent();
            DataContext = ViewModel;
        }

        private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
}

