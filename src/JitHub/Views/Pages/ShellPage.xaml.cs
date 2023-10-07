using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace JitHub.Views.Pages;

public sealed partial class ShellPage : Page
{
    private Brush _titleBarModalBrush = new SolidColorBrush(Color.FromArgb(77, 0, 0, 0));
    public ShellPage()
    {
        this.InitializeComponent();
        // Set XAML element as a draggable region.
        
        Window.Current.SetTitleBar(TitleBar);
        ViewModel.LoadApplication(new RelayCommand(OpenModal), new RelayCommand(CloseModal));
        var notificationService = Ioc.Default.GetService<INotificationService>();
        notificationService.Register(new RelayCommand<string>(PushNotification));
        
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView()
            .GetAnimation("AppLogoAnimation");
        if (animation != null)
        {
            animation.TryStart(AppLogoShellPage);
        }
        ViewModel.RegisterSearchDebounce(SearchBox);
        _ = ViewModel.OnNavigatedTo();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        ConnectedAnimationService.GetForCurrentView()
            .PrepareToAnimate("AppLogoLogoutAnimation", AppLogoShellPage);
    }

    private void OpenModal()
    {
        Modal.Visibility = Visibility.Visible;
        TitleBar.Background = _titleBarModalBrush;
        SearchBox.IsEnabled = false;
    }

    private void CloseModal()
    {
        Modal.Visibility = Visibility.Collapsed;
        TitleBar.Background = new SolidColorBrush(Colors.Transparent);
        SearchBox.IsEnabled = true;
    }

    private void PushNotification(string message)
    {
        InAppNotification.Show(message, 3000);   
    }

    private void Page_SizeChanged(object sender, Windows.UI.Xaml.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 768)
        {
            VisualStateManager.GoToState(this, "WideLayout", false);
        }
        else
        {
            VisualStateManager.GoToState(this, "NarrowLayout", false);
        }
    }
}
