using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class PullRequestButton : UserControl
    {
        private NavigationService _navigationService;

        public static DependencyProperty PullRequestProperty = DependencyProperty.Register(
            nameof(PullRequest),
            typeof(Octokit.PullRequest),
            typeof(PullRequestButton),
            new PropertyMetadata(default(Octokit.Issue), null)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(Repository),
            typeof(PullRequestButton),
            new PropertyMetadata(default(Repository), null)
        );

        public Octokit.PullRequest PullRequest
        {
            get => (Octokit.PullRequest)GetValue(PullRequestProperty);
            set => SetValue(PullRequestProperty, value);
        }
        public Repository Repo
        {
            get => (Repository)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public PullRequestButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>();
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.PullRequestPage, new PullRequestPageNavArg(Repo, PullRequest.Number), Repo));
        }
    }
}
