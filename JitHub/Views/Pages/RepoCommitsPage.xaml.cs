using JitHub.Models.NavArgs;
using JitHub.ViewModels.CommitViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace JitHub.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoCommitsPage : Page
    {
        public RepoCommitsViewModel ViewModel { get; set; }
        public RepoCommitsPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel = new RepoCommitsViewModel((CommitPageNavArg)e.Parameter);
            DataContext = ViewModel;
            ViewModel.Load();
        }
    }
}
