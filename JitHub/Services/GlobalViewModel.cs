using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.Services
{
    public class GlobalViewModel : ObservableObject
    {
        private const string DEV_MODE_TOGGGLE = "DEV_MODE_KEY";
        private ISettingService _settingService;
        private INotificationService _notificationService;
        private bool _devMode;

        public bool DevMode
        {
            get => _devMode;
            private set
            {
                SetProperty(ref _devMode, value);
            }
        }

        public GlobalViewModel(ISettingService settingServices, INotificationService notificationService)
        {
            _settingService = settingServices;
            _notificationService = notificationService;
            DevMode = _settingService.Get<bool>(DEV_MODE_TOGGGLE);
        }

        public void ToggleDevMode()
        {
            DevMode = !DevMode;
            _settingService.Save(DEV_MODE_TOGGGLE, DevMode);
            var state = DevMode ? "on" : "off";
            _notificationService.Push($"Dev mode has been turned {state}");
        }
    }
}
