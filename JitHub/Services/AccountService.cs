namespace JitHub.Services
{
    public class AccountService : IAccountService
    {
        private ISettingService _settings;
        private static string userIdKey = "USER_ID";
        public static string doNotWarnDeleteRepoKey = "DO_NOT_WARN_DELETE_REPO";
        public AccountService(ISettingService settings)
        {
            _settings = settings;
        }
        
        public void RemoveUser()
        {
            _settings.Save(userIdKey, string.Empty);
        }

        public void SaveUser(long userId)
        {
            _settings.Save(userIdKey, userId);
            _settings.Save(doNotWarnDeleteRepoKey, false);
        }

        public int GetUser()
        {
            return _settings.Get<int>(userIdKey);
        }
    }
}
