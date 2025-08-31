namespace JitHub.Services
{
    public interface IAccountService
    {
        void RemoveUser();
        void SaveUser(long userId);
        int GetUser();
    }
}
