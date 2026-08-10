using JitHub.Models.Base;
using JitHub.Models.LegacyGitHub;
using System.Linq;
using System.Windows.Input;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class SelectableUser : SelectableItem
    {
        public string Login { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;

        public string AutomationId => $"SelectableUser_{new string(Login.Where(char.IsLetterOrDigit).ToArray())}";

        public string AutomationName => $"Select user {Login}";

        public SelectableUser(string login, string url, ICommand? command)
        {
            Login = login;
            AvatarUrl = url;
            ApplySelectionCommand(command);
            Type = "User";
        }

        public SelectableUser(Collaborator collaborator, ICommand? command)
        {
            Login = collaborator.Login;
            AvatarUrl = collaborator.AvatarUrl;
            ApplySelectionCommand(command);
            Type = "User";
        }

        public SelectableUser(RepositoryContributor user, ICommand? command)
        {
            Login = user.Login;
            AvatarUrl = user.AvatarUrl;
            ApplySelectionCommand(command);
            Type = "User";
        }

        public SelectableUser(User user, ICommand? command)
        {
            Login = user.Login;
            AvatarUrl = user.AvatarUrl;
            ApplySelectionCommand(command);
            Type = "User";
        }

        private void ApplySelectionCommand(ICommand? command)
        {
            if (command is not null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
        }
    }
}
