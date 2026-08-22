using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using JitHub.Models.LegacyGitHub;
using PullRequestUpdate = JitHub.Models.LegacyGitHub.PullRequestUpdate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
public partial class RepoPullRequestEditViewModel : RepoViewModel
    {
        private string _title = string.Empty;
        private string _body = string.Empty;
        private string _selectedBodyView = "Write";
        private PullRequest _pullRequest = null!;
        private readonly ModalService _modalService;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string Body
        {
            get => _body;
            set => SetProperty(ref _body, value);
        }

        public string SelectedBodyView
        {
            get => _selectedBodyView;
            set => SetProperty(ref _selectedBodyView, value);
        }
        public PullRequest PullRequest
        {
            get => _pullRequest;
            set
            {
                SetProperty(ref _pullRequest, value);
                Title = value?.Title ?? string.Empty;
                Body = value?.Body ?? string.Empty;
            }
        }

        public ICommand CloseCommand { get; }
        public ICommand SubmitCommand { get; }
        public ICommand? SuccessCallbackCommand { get; set; }

        public MarkdownDocumentSource? MarkdownSource => Repo?.Owner?.Login is string owner &&
            !string.IsNullOrWhiteSpace(owner) &&
            !string.IsNullOrWhiteSpace(Repo.Name) && PullRequest is not null
                ? MarkdownDocumentSourceFactory.CreateRepositoryDocument(
                    "pull-request-edit-draft",
                    PullRequest.Id.ToString(),
                    owner,
                    Repo.Name,
                    PullRequest.Base?.Ref ?? Repo.DefaultBranch)
                : null;

        public RepoPullRequestEditViewModel() : base()
        {
            CloseCommand = new RelayCommand(OnClose);
            SubmitCommand = new AsyncRelayCommand(OnSubmit);
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
        }

        public void OnNavChange(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            SelectedBodyView = args.InvokedItem as string ?? SelectedBodyView;
        }

        private void OnClose()
        {
            Body = string.Empty;
            Title = string.Empty;
            _modalService.Close();
        }

        private async Task OnSubmit()
        {
            if (string.IsNullOrWhiteSpace(Title))
                return;
            else
            {
                await GitHubService.UpdatePullRequest(Repo.Id, PullRequest.Number, new PullRequestUpdate() { Title = Title, Body = Body });
            }
            if (SuccessCallbackCommand?.CanExecute(null) == true)
            {
                SuccessCallbackCommand.Execute(null);
            }
            OnClose();
        }
    }
}




