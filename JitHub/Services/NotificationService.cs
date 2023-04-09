using System.Windows.Input;

namespace JitHub.Services
{
    public class NotificationService : INotificationService
    {
        private ICommand _pushCommand;
        
        public void Push(string message)
        {
            if (_pushCommand != null && _pushCommand.CanExecute(message))
            {
                _pushCommand.Execute(message);
            }
        }

        public void Register(ICommand pushCommand)
        {
            _pushCommand = pushCommand;
        }
    }
}
