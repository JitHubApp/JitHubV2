using JitHub.Models;
using System.Windows.Input;

namespace JitHub.Services
{
    public interface ICommandService
    {
        ICommand GetCommand(JitHubCommand commandName);
        void RegisterCommand(JitHubCommand commandName, ICommand command);
    }
}
