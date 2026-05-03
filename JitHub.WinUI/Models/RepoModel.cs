using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System;
using System.Windows.Input;

namespace JitHub.Models
{
    public class RepoModel : ObservableObject
    {
        private Repository _repository = null!;
        private readonly NavigationService _navigationService;
        private ICommand _detailNavigationCommand = null!;

        public Repository Repository { get => _repository; set => SetProperty(ref _repository, value); }
        public ICommand DetailNavigationCommand { get => _detailNavigationCommand; set => SetProperty(ref _detailNavigationCommand, value); }

        public RepoModel(Repository repo)
        {
            Repository = repo ?? throw new ArgumentNullException(nameof(repo));
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            DetailNavigationCommand = new RelayCommand(NavigateToDetailPage);
        }

        public void NavigateToDetailPage()
        {
            _navigationService.NavigateTo(Repository.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, Repository));
        }
    }
}

