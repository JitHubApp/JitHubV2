using JitHub.Models.Base;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.Views.Pages.IssuePage;
using IssueModel = JitHub.Models.LegacyGitHub.Issue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Issue
{
    public sealed partial class RepoIssueDetail : UserControl
    {
        public static readonly DependencyProperty IssueProperty = DependencyProperty.Register(
            "Issue",
            typeof(object),
            typeof(RepoIssueDetail),
            new PropertyMetadata(null, OnViewModelChanged));

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RepoIssueDetail self && e.NewValue is RepoSelectableItemModel<IssueModel> issue)
            {
                var viewmodel = new RepoIssueDetailViewModel(issue);
                self.IssueDetailPage.Navigate(typeof(IssueDetailPage), viewmodel);
            }
        }

        public RepoSelectableItemModel<IssueModel> Issue
        {
            get => (RepoSelectableItemModel<IssueModel>)GetValue(IssueProperty);
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


