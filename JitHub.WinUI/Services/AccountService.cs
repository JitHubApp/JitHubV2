namespace JitHub.Services;

public class AccountService : IAccountService
{
    private const string UserIdKey = "USER_ID";

    public const string DoNotWarnDeleteRepoKey = "DO_NOT_WARN_DELETE_REPO";
    public const string doNotWarnDeleteRepoKey = DoNotWarnDeleteRepoKey;

    private readonly ISettingService _settings;

    public AccountService(ISettingService settings)
    {
        _settings = settings;
    }

    public void RemoveUser()
    {
        _settings.Save(UserIdKey, 0L);
    }

    public void SaveUser(long userId)
    {
        _settings.Save(UserIdKey, userId);
        _settings.Save(DoNotWarnDeleteRepoKey, false);
    }

    public long GetUser()
    {
        return _settings.Get<long>(UserIdKey);
    }
}
