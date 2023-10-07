using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Windows.Input;

namespace JitHub.Models
{
    public class RepoModel : ObservableObject
    {
        private Repository _repository;
        private NavigationService _navigationService;
        private ICommand _detailNavigationCommand;

        public Repository Repository { get => _repository; set => SetProperty(ref _repository, value); }
        public ICommand DetailNavigationCommand { get => _detailNavigationCommand; set => SetProperty(ref _detailNavigationCommand, value); }

        public RepoModel(Repository repo)
        {
            Repository = repo;
            _navigationService = Ioc.Default.GetService<NavigationService>();
            DetailNavigationCommand = new RelayCommand(NavigateToDetailPage);
        }

        public void NavigateToDetailPage()
        {
            _navigationService.NavigateTo(Repository.GetRepositoryFullName(), typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, Repository));
        }
    }
}
