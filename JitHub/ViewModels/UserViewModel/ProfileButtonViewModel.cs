using JitHub.ViewModels.Base;
using Octokit;
using System.Windows.Input;

namespace JitHub.ViewModels.UserViewModel
{
    public class ProfileButtonViewModel : LoadableViewModel<User>
    {
        public ICommand GoToProfilePageCommand { get; set; }
    }
}
