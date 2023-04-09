using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Commit
{
    public sealed partial class CommitButton : UserControl
    {
        private NavigationService _navigationService;

        public static DependencyProperty CommitIdProperty = DependencyProperty.Register(
            nameof(CommitId),
            typeof(string),
            typeof(CommitButton),
            new PropertyMetadata(default(string), null)
        );

        public static DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(CommitButton),
            new PropertyMetadata(default(string), null)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(Repository),
            typeof(CommitButton),
            new PropertyMetadata(default(Repository), null)
        );

        public string CommitId
        {
            get => (string)GetValue(CommitIdProperty);
            set => SetValue(CommitIdProperty, value);
        }
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        public Repository Repo
        {
            get => (Repository)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public CommitButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>();
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CommitPage, CommitPageNavArg.CreateWithGitRef(Repo, CommitId), Repo));
        }
    }
}
