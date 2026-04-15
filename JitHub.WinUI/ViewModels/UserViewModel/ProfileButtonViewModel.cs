using JitHub.WinUI.ViewModels.Base;
using Octokit;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.UserViewModel
{
    public class ProfileButtonViewModel : LoadableViewModel<User>
    {
        public ICommand? GoToProfilePageCommand { get; set; }
    }
}

