using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using PullRequestModel = JitHub.Models.LegacyGitHub.PullRequest;
using RepositoryModel = JitHub.Models.LegacyGitHub.Repository;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
{
    public sealed partial class PullRequestButton : UserControl
    {
        private readonly NavigationService _navigationService;

        public static DependencyProperty PullRequestProperty = DependencyProperty.Register(
            nameof(PullRequest),
            typeof(PullRequestModel),
            typeof(PullRequestButton),
            new PropertyMetadata(default(PullRequestModel), OnPullRequestChanged)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(RepositoryModel),
            typeof(PullRequestButton),
            new PropertyMetadata(default(RepositoryModel), null)
        );

        public PullRequestModel? PullRequest
        {
            get => (PullRequestModel?)GetValue(PullRequestProperty);
            set => SetValue(PullRequestProperty, value);
        }
        public RepositoryModel? Repo
        {
            get => (RepositoryModel?)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public PullRequestButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
        }

        private static void OnPullRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PullRequestButton self)
            {
                self.Bindings.Update();
            }
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            if (Repo is null || PullRequest is null)
            {
                return;
            }

            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.PullRequestPage, new PullRequestPageNavArg(Repo, PullRequest.Number), Repo));
        }
    }
}



