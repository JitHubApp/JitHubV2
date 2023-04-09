using Octokit;
using System.Linq;
using System.Windows.Input;
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class PullRequestForm : UserControl
    {
        public PullRequestForm(Repository repo, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Repo = repo;
            ViewModel.SuccessCallbackCommand = callback;
        }
    }
}
