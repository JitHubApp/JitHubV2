using System.Windows.Input;

namespace JitHub.Services
{
    public interface INotificationService
    {
        void Push(string message);
        void Register(ICommand pushCommand);
    }
}
