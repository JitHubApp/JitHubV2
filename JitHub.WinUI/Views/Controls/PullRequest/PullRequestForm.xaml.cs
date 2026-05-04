using JitHub.Models.LegacyGitHub;
using RepositoryModel = JitHub.Models.LegacyGitHub.Repository;
using System.Linq;
using System.Windows.Input;
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
{
    public sealed partial class PullRequestForm : UserControl
    {
        public PullRequestForm(RepositoryModel repo, ICommand callback)
        {
            this.InitializeComponent();
            ViewModel.Repo = repo;
            ViewModel.SuccessCallbackCommand = callback;
        }

        private void PullRequestForm_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (ViewModel.LoadCommand.CanExecute(null))
            {
                ViewModel.LoadCommand.Execute(null);
            }
        }
    }
}

