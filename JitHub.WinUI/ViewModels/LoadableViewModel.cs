using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Core.Models.GitHub.Users;
using JitHub.Core.Services.Authentication;
using JitHub.WinUI.Models.LegacyGitHub;
using JitHub.WinUI.Services.GitHub;
using System;

namespace JitHub.WinUI.ViewModels
{
    public abstract class LoadableViewModel<T> : ObservableObject
    {
        private bool _loading;
        private readonly IGitHubService _gitHubService;
        private readonly IAuthService _authService;
        private T _model = default!;
        
        public bool Loading
        {
            get => _loading;
            set => SetProperty(ref _loading, value);
        }

        public IGitHubService GitHubService
        {
            get => _gitHubService;
        }

        public T Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }

        public GitHubUser? User => _authService.AuthenticatedUser;

        public LoadableViewModel<T> Item => this;

        public LoadableViewModel()
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
            _authService = Ioc.Default.GetService<IAuthService>()
                ?? throw new InvalidOperationException("IAuthService is not registered.");
        }
    }
}


