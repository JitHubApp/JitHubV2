using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.UI.Helpers;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JitHub.WinUI.ViewModels
{
    public class LoginViewModel : ObservableObject
    {
        private readonly IAuthService _authService;
        private readonly ThemeListener _listener;
        private ImageSource _source;
        private readonly ImageSource _lightSource = new BitmapImage(new Uri("ms-appx:///Assets/pro_x_light.png"));
        private readonly ImageSource _darkSource = new BitmapImage(new Uri("ms-appx:///Assets/pro_x_dark.png"));
        public ICommand LoginCommand { get; }
        public ImageSource Source
        {
            get => _source;
            set => SetProperty(ref _source, value);
        }

        public LoginViewModel()
        {
            _authService = Ioc.Default.GetService<IAuthService>()
                ?? throw new InvalidOperationException("IAuthService is not registered.");
            LoginCommand = new AsyncRelayCommand(Login);
            _listener = new ThemeListener();
            _listener.ThemeChanged += ChangeTheme;
            _source = _lightSource;
            ChangeTheme(_listener);
        }

        private void ChangeTheme(ThemeListener sender)
        {
            if (sender.CurrentTheme == ApplicationTheme.Light)
            {
                Source = _lightSource;
            }
            else
            {
                Source = _darkSource;
            }
        }

        private async Task Login()
        {
            await _authService.Authenticate();
        }
    }
}



