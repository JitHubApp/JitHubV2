using JitHub.Converters.Activities;
using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Code
{
    public sealed partial class CodeButton : UserControl
    {
        private NavigationService _navigationService;
        public static DependencyProperty RepoProperty = DependencyProperty.Register(
            nameof(Repo),
            typeof(Repository),
            typeof(CodeButton),
            new PropertyMetadata(default(Repository), null)
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

        public Repository Repo
        {
            get => (Repository)GetValue(RepoProperty);
            set => SetValue(RepoProperty, value);
        }
        public string Ref
        {
            get => (string)GetValue(RefProperty);
            set => SetValue(RefProperty, value);
        }
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        public CodeButton()
        {
            this.InitializeComponent();
            _navigationService = Ioc.Default.GetService<NavigationService>();
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            var gitRef = RefFullStringToBranchConverter.ConvertFromRefToBranch(Ref);
            _navigationService.NavigateTo(Repo.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, CodeViewerNavArg.CreateWithGitRef(Repo, gitRef), Repo));
        }
    }
}
