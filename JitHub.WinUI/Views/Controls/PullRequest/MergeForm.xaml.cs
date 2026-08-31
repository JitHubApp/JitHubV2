using JitHub.Models.LegacyGitHub;
using PullRequestMergeMethod = JitHub.Models.LegacyGitHub.PullRequestMergeMethod;
using PullRequestModel = JitHub.Models.LegacyGitHub.PullRequest;
using RepositoryModel = JitHub.Models.LegacyGitHub.Repository;
using System.Windows.Input;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
{
    public sealed partial class MergeForm : UserControl, IModalSessionAware
    {
        public MergeForm(RepositoryModel repo, PullRequestModel pullRequest, PullRequestMergeMethod selectedItem, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Init(repo, pullRequest, selectedItem, callback);
            Bindings.Update();
        }

        public void AttachModalSession(ModalSession session) => ViewModel.AttachModalSession(session);
    }
}



