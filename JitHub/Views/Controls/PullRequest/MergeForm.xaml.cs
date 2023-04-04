using Octokit;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class MergeForm : UserControl
    {
        public MergeForm(Repository repo, Octokit.PullRequest pullRequest, PullRequestMergeMethod selectedItem, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Init(repo, pullRequest, selectedItem, callback);
        }
    }
}
