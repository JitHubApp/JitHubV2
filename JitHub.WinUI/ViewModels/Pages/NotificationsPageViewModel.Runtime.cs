using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class NotificationsPageViewModel
{
    public NotificationsPageViewModel()
        : this(
            ((App)App.Current).GetService<IGitHubNotificationQueryService>(),
            ((App)App.Current).GetService<IAuthService>(),
            ((App)App.Current).GetService<IAccountService>(),
            ((App)App.Current).GetService<ITelemetryService>(),
            ((App)App.Current).GetService<NotificationInboxState>(),
            ((App)App.Current).GetService<ShellPageViewModel>().OpenNotification,
            ((App)App.Current).GetService<IApplicationTaskCoordinator>(),
            ((App)App.Current).GetService<ShellPageViewModel>().PrefetchNotificationAsync)
    {
    }
}
