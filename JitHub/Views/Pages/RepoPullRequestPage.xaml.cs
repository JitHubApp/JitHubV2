using JitHub.Models.NavArgs;
using Octokit;
using Windows.UI.Xaml.Navigation;
using Page = Windows.UI.Xaml.Controls.Page;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoPullRequestPage : Page
    {
        public RepoPullRequestPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var arg = (PullRequestPageNavArg)e.Parameter;
            ViewModel.Init(arg);
        }
    }
}
