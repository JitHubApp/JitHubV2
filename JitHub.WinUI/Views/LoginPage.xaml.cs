using CommunityToolkit.WinUI.UI.Helpers;
using System;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.WinUI.Views
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            App app = (App)Application.Current;
            app.CurrentMainWindow.SetPageTitleBar(TitleBar);

            IAuthService authService = app.GetService<IAuthService>();
            IAccountService accountService = app.GetService<IAccountService>();
            if (authService.Authenticated || authService.CheckAuth(accountService.GetUser()))
            {
                app.GetService<NavigationService>().GoHome();
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate("AppLogoAnimation", AppLogoLoginPage);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView()
                .GetAnimation("AppLogoLogoutAnimation");
            if (animation != null)
            {
                animation.TryStart(AppLogoLoginPage);
            }
        }

        

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 900)
            {
                VisualStateManager.GoToState(this, "WideLayout", false);
            }
            else
            {
                VisualStateManager.GoToState(this, "NarrowLayout", false);
            }
        }
    }
}


