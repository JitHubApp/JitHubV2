using JitHub.Services;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.ViewModels
{
    public class DevConsoleViewModel : ObservableObject
    {
        private string _token;
        private IGitHubService _githubService;
        private IAuthService _authService;

        public string Token
        {
            get => _token;
            set => SetProperty(ref _token, value);
        }

        public DevConsoleViewModel()
        {
            _githubService = Ioc.Default.GetService<IGitHubService>();
            _authService = Ioc.Default.GetService<IAuthService>();
            Token = _authService.GetToken(_authService.AuthenticatedUser.Id);
        }
    }
}
