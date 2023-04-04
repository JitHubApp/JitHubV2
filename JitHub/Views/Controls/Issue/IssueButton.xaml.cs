using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Issue
{
    public sealed partial class IssueButton : UserControl
    {
        private NavigationService _navigationService;

        public static DependencyProperty IssueProperty = DependencyProperty.Register(
            nameof(Issue),
            typeof(Octokit.Issue),
            typeof(IssueButton),
            new PropertyMetadata(default(Octokit.Issue), null)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(Repository),
            typeof(IssueButton),
            new PropertyMetadata(default(Repository), null)
        );

        public Octokit.Issue Issue
        {
            get => (Octokit.Issue)GetValue(IssueProperty);
            set => SetValue(IssueProperty, value);
        }
        public Repository Repo
        {
            get => (Repository)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public IssueButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>();
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.IssuePage, new IssueNavArg(Repo, Issue.Number), Repo));
        }
    }
}
