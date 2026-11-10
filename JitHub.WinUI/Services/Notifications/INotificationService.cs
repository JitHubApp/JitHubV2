using System.Windows.Input;

namespace JitHub.WinUI.Services.Notifications;

public interface INotificationService
{
    void Push(string message);

    void Register(ICommand pushCommand);
}
