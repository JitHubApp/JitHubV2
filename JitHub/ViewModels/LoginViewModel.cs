using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp.UI.Helpers;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace JitHub.ViewModels
{
    public class LoginViewModel : ObservableObject
    {
        private IAuthService _authService;
        private ThemeListener _listener;
        private ImageSource _source;
        private ImageSource _lightSource = new BitmapImage(new Uri("ms-appx:///Assets/pro_x_light.png"));
        private ImageSource _darkSource = new BitmapImage(new Uri("ms-appx:///Assets/pro_x_dark.png"));
        public ICommand LoginCommand { get; }
        public ImageSource Source
        {
            get => _source;
            set => SetProperty(ref _source, value);
        }

        public LoginViewModel()
        {
            _authService = Ioc.Default.GetService<IAuthService>();
            LoginCommand = new AsyncRelayCommand(Login);
            _listener = new ThemeListener();
            _listener.ThemeChanged += ChangeTheme;
            ChangeTheme(_listener);
        }

        private void ChangeTheme(ThemeListener sender)
        {
            if (_listener.CurrentTheme == ApplicationTheme.Light)
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
