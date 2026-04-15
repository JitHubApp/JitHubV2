using JitHub.Models.NavArgs;
using JitHub.WinUI.ViewModels.CommitViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace JitHub.WinUI.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoCommitsPage : Page
    {
        public RepoCommitsViewModel? ViewModel { get; set; }
        public RepoCommitsPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is CommitPageNavArg args)
            {
                ViewModel = new RepoCommitsViewModel(args);
                DataContext = ViewModel;
                _ = ViewModel.Load();
            }
        }
    }
}


