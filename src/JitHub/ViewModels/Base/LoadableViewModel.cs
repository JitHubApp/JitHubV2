using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;

namespace JitHub.ViewModels.Base
{
    public abstract class LoadableViewModel<T> : ObservableObject
    {
        private bool _loading;
        private IGitHubService _gitHubService;
        private IAuthService _authService;
        private T _model;
        
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

        public User User => _authService.AuthenticatedUser;

        public LoadableViewModel<T> Item => this;

        public LoadableViewModel()
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
            _authService = Ioc.Default.GetService<IAuthService>();
        }
    }
}
