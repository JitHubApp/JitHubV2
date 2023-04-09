using JitHub.Models.Base;
using Octokit;
using System.Windows.Input;

namespace JitHub.Models
{
    public class SelectableUser : SelectableItem
    {
        public string Login { get; set; }
        public string AvatarUrl { get; set; }

        public SelectableUser(string login, string url, ICommand command)
        {
            Login = login;
            AvatarUrl = url;
            if (command != null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
            Type = "User";
        }

        public SelectableUser(Collaborator collaborator, ICommand command)
        {
            Login = collaborator.Login;
            AvatarUrl = collaborator.AvatarUrl;
            if (command != null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
            Type = "User";
        }

        public SelectableUser(RepositoryContributor user, ICommand command)
        {
            Login = user.Login;
            AvatarUrl = user.AvatarUrl;
            if (command != null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
            Type = "User";
        }

        public SelectableUser(User user, ICommand command)
        {
            Login = user.Login;
            AvatarUrl = user.AvatarUrl;
            if (command != null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
            Type = "User";
        }
    }
}
