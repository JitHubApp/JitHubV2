using JitHub.Models.Base;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Controls.PullRequest;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.ViewModels.PullRequestViewModels
{
    public class RepoPullRequestDetailViewModel : RepoViewModel
    {
        private PullRequest _pullRequest;
        private Frame _frame;
        private ICommand _refreshCommand;
        private ModalService _modalService;

        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }
        public Frame Frame
        {
            get => _frame;
            set => SetProperty(ref _frame, value);
        }

        public ICommand EditCommand { get; }

        public RepoPullRequestDetailViewModel(RepoSelectableItemModel<PullRequest> pullRequestModel)
        {
            PullRequest = pullRequestModel.Model;
            Repo = pullRequestModel.Repository;
            _refreshCommand = new AsyncRelayCommand(Refresh);
            _modalService = Ioc.Default.GetService<ModalService>();
            EditCommand = new RelayCommand(EditPullRequest);
        }

        private void EditPullRequest()
        {
            _modalService.Open("Edit Pull Request", new PullRequestEditForm(Repo, PullRequest, _refreshCommand));
        }

        public void GoToConversationPage()
        {
            Frame.Navigate(typeof(PullRequestConversationPage), new PullRequestConvPageNavArg(Repo, PullRequest, _refreshCommand));
        }

        private async Task Refresh()
        {
            Loading = true;
            PullRequest = await GitHubService.GetPullRequest(Repo.Owner.Login, Repo.Name, PullRequest.Number);
            GoToConversationPage();
            Loading = false;
        }

        

        public void RepoPullRequestDetailNavView_BackRequested(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewBackRequestedEventArgs args)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        public void RepoPullRequestDetailNavView_ItemInvoked(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewItemInvokedEventArgs args)
        {
            switch (args.InvokedItem as string)
            {
                case "Conversation":
                    GoToConversationPage();
                    break;
                case "Commits":
                    Frame.Navigate(typeof(PullRequestCommitsPage), (Repo, PullRequest));
                    break;
                default:
                    break;
            }
        }
    }
}
