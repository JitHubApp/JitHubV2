using Octokit;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class PullRequestEditForm : UserControl
    {
        public PullRequestEditForm(Repository repo, Octokit.PullRequest existingPullRequest, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Repo = repo;
            ViewModel.PullRequest = existingPullRequest;
            ViewModel.SuccessCallbackCommand = callback;
        }
    }
}
