namespace JitHub.Core.Services.Accounts;

public interface IAccountService
{
    void RemoveUser();

    void SaveUser(long userId);

    long GetUser();
}
