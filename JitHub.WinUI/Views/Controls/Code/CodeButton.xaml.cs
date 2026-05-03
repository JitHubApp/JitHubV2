using JitHub.WinUI.Converters.Activities;
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

namespace JitHub.WinUI.Views.Controls.Code
{
    public sealed partial class CodeButton : UserControl
    {
        private readonly NavigationService _navigationService;
        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(RepositoryModel),
            typeof(CodeButton),
            new PropertyMetadata(default(RepositoryModel), null)
        );
        public static DependencyProperty RefProperty = DependencyProperty.Register(
            nameof(Ref),
            typeof(string),
            typeof(CodeButton),
            new PropertyMetadata(default(string), null)
        );
        public static DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(CodeButton),
            new PropertyMetadata(default(string), null)
        );

        public RepositoryModel? Repo
        {
            get => (RepositoryModel?)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public string? Ref
        {
            get => (string?)GetValue(RefProperty);
            set => SetValue(RefProperty, value);
        }
        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        public CodeButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            if (Repo is null)
            {
                return;
            }

            var gitRef = RefFullStringToBranchConverter.ConvertFromRefToBranch(Ref);
            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, CodeViewerNavArg.CreateWithGitRef(Repo, gitRef), Repo));
        }
    }
}



