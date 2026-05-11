using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using RepositoryModel = JitHub.Models.LegacyGitHub.Repository;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Commit
{
    public sealed partial class CommitButton : UserControl
    {
        private readonly NavigationService _navigationService;

        public static DependencyProperty CommitIdProperty = DependencyProperty.Register(
            nameof(CommitId),
            typeof(string),
            typeof(CommitButton),
            new PropertyMetadata(default(string), OnBindablePropertyChanged)
        );

        public static DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(CommitButton),
            new PropertyMetadata(default(string), OnBindablePropertyChanged)
        );

        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(RepositoryModel),
            typeof(CommitButton),
            new PropertyMetadata(default(RepositoryModel), null)
        );

        public string? CommitId
        {
            get => (string?)GetValue(CommitIdProperty);
            set => SetValue(CommitIdProperty, value);
        }
        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        public RepositoryModel? Repo
        {
            get => (RepositoryModel?)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public CommitButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
        }

        private static void OnBindablePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CommitButton self)
            {
                self.Bindings.Update();
            }
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            if (Repo is null || string.IsNullOrWhiteSpace(CommitId))
            {
                return;
            }

            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CommitPage, CommitPageNavArg.CreateWithGitRef(Repo, CommitId), Repo));
        }
    }
}



