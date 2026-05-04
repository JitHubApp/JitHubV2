using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.WinUI.ViewModels
{
    public class DevConsoleViewModel : ObservableObject
    {
        private string _token = string.Empty;
        private readonly IAuthService _authService;

        public string Token
        {
            get => _token;
            set => SetProperty(ref _token, value);
        }

        public DevConsoleViewModel()
        {
            _authService = Ioc.Default.GetService<IAuthService>()
                ?? throw new InvalidOperationException("IAuthService is not registered.");
            Token = _authService.GetToken(_authService.AuthenticatedUser?.Id ?? 0) ?? string.Empty;
        }
    }
}


