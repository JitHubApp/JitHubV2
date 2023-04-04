using JitHub.Services;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.ViewModels.PullRequestViewModels
{
    public class RepoPullRequestEditViewModel : RepoViewModel
    {
        private string _title;
        private string _body;
        private string _selectedBodyView = "Write";
        private PullRequest _pullRequest;
        private ModalService _modalService;

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
                Title = value?.Title;
                Body = value?.Body;
            }
        }

        public ICommand CloseCommand { get; }
        public ICommand SubmitCommand { get; }
        public ICommand SuccessCallbackCommand { get; set; }

        public RepoPullRequestEditViewModel() : base()
        {
            CloseCommand = new RelayCommand(OnClose);
            SubmitCommand = new AsyncRelayCommand(OnSubmit);
            _modalService = Ioc.Default.GetService<ModalService>();
        }

        public void OnNavChange(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            SelectedBodyView = (string)args.InvokedItem;
        }

        private void OnClose()
        {
            Body = "";
            Title = "";
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
            SuccessCallbackCommand.Execute(null);
            OnClose();
        }
    }
}
