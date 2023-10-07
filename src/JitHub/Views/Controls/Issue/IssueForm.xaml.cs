using Octokit;
using System.Windows.Input;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Issue
{
    public sealed partial class IssueForm : UserControl
    {
        public IssueForm(ICommand onSubmitCommand, string title = "", string text = "", Octokit.Issue issue = null, long repoId = 0)
        {
            this.InitializeComponent();
            ViewModel.Title = title;
            ViewModel.Text = text;
            ViewModel.Issue = issue;
            ViewModel.RepoId = repoId;
            ViewModel.SubmitCommand = onSubmitCommand;
        }
    }
}
