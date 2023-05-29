using Microsoft.Toolkit.Uwp.Helpers;

namespace JitHub.Services
{
    public class SettingService : ISettingService
    {
        private ApplicationDataStorageHelper _store;
        public SettingService()
        {
            _store = ApplicationDataStorageHelper.GetCurrent(new Microsoft.Toolkit.Helpers.SystemSerializer());
        }
        public T Get<T>(string key)
        {
            return _store.Read<T>(key);
        }
        
        public void Save<T>(string key, T value)
        {
            _store.Save(key, value);
        }
    }
}
  