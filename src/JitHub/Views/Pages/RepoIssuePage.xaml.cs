using JitHub.Models;
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
    public sealed partial class RepoIssuePage : Page
    {
        public RepoIssuePage()
        {
            this.InitializeComponent();
        }

        override protected void OnNavigatedTo(NavigationEventArgs e)
        {
            var arg = (IssueNavArg)e.Parameter;
            ViewModel.Init(arg);
        }
    }
}
