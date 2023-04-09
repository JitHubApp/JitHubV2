using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.Views;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class LoginPage : Page
{
    public LoginPage()
    {
        this.InitializeComponent();
        App.Window.SetTitleBar(TitleBar);
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
