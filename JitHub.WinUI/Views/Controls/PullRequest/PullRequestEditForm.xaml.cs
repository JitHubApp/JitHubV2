using Octokit;
using PullRequestModel = Octokit.PullRequest;
using RepositoryModel = Octokit.Repository;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
{
    public sealed partial class PullRequestEditForm : UserControl
    {
        public PullRequestEditForm(RepositoryModel repo, PullRequestModel existingPullRequest, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Repo = repo;
            ViewModel.PullRequest = existingPullRequest;
            ViewModel.SuccessCallbackCommand = callback;
        }
    }
}

