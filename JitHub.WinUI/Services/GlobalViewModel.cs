using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.Services;

public sealed class GlobalViewModel : ObservableObject
{
    private const string DevModeToggleKey = "DEV_MODE_KEY";

    private readonly ISettingService _settingService;
    private readonly INotificationService _notificationService;
    private bool _devMode;

    public GlobalViewModel(ISettingService settingServices, INotificationService notificationService)
    {
        _settingService = settingServices;
        _notificationService = notificationService;
        _devMode = _settingService.Get<bool>(DevModeToggleKey);
    }

    public bool DevMode
    {
        get => _devMode;
        set
        {
            if (SetProperty(ref _devMode, value))
            {
                ToggledDevMode();
            }
        }
    }

    public void ToggledDevMode()
    {
        _settingService.Save(DevModeToggleKey, DevMode);
        string state = DevMode ? "on" : "off";
        _notificationService.Push($"Dev mode has been turned {state}");
    }
}
