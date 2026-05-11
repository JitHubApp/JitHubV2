using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using IssueModel = JitHub.Models.LegacyGitHub.Issue;
using RepositoryModel = JitHub.Models.LegacyGitHub.Repository;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Issue
{
    public sealed partial class IssueButton : UserControl
    {
        private readonly NavigationService _navigationService;

        public static DependencyProperty IssueProperty = DependencyProperty.Register(
            nameof(Issue),
            typeof(IssueModel),
            typeof(IssueButton),
            new PropertyMetadata(default(IssueModel), OnIssueChanged)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(RepositoryModel),
            typeof(IssueButton),
            new PropertyMetadata(default(RepositoryModel), null)
        );

        public IssueModel? Issue
        {
            get => (IssueModel?)GetValue(IssueProperty);
            set => SetValue(IssueProperty, value);
        }
        public RepositoryModel? Repo
        {
            get => (RepositoryModel?)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public IssueButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
        }

        private static void OnIssueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IssueButton self)
            {
                self.Bindings.Update();
            }
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            if (Repo is null || Issue is null)
            {
                return;
            }

            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.IssuePage, new IssueNavArg(Repo, Issue.Number), Repo));
        }
    }
}



