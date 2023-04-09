namespace JitHub.Services
{
    public interface IAccountService
    {
        void RemoveUser();
        void SaveUser(int userId);
        int GetUser();
    }
}
