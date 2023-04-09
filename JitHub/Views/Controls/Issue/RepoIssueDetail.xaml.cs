using JitHub.Models.Base;
using JitHub.ViewModels.IssueViewModels;
using JitHub.Views.Pages.IssuePage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Issue
{
    public sealed partial class RepoIssueDetail : UserControl
    {
        public static readonly DependencyProperty IssueProperty = DependencyProperty.Register(
            "Issue",
            typeof(RepoSelectableItemModel<Octokit.Issue>),
            typeof(RepoIssueDetail),
            new PropertyMetadata(default(RepoSelectableItemModel<Octokit.Issue>), OnViewModelChanged));

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RepoIssueDetail self && e.NewValue != null)
            {
                var issue = self.DataContext as RepoSelectableItemModel<Octokit.Issue>;
                var viewmodel = new RepoIssueDetailViewModel(issue);
                self.IssueDetailPage.Navigate(typeof(IssueDetailPage), viewmodel);
            }
        }

        public RepoSelectableItemModel<Octokit.Issue> Issue
        {
            get => (RepoSelectableItemModel<Octokit.Issue>)GetValue(IssueProperty);
            set
            {
                SetValue(IssueProperty, value);
            }
        }

        public RepoIssueDetail()
        {
            this.InitializeComponent();
        }


    }
}
