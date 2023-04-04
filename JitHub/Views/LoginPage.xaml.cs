using Microsoft.Toolkit.Uwp.UI.Helpers;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.Views
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            this.InitializeComponent();
            Window.Current.SetTitleBar(TitleBar);
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
