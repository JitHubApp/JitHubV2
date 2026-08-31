using System.Windows.Input;
using JitHub.Services;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Repo
{
    public sealed partial class RepoForm : UserControl, IModalSessionAware
    {
        public RepoForm(ICommand refreshcommand)
        {
            this.InitializeComponent();
            ViewModel.Init(refreshcommand);
        }

        public void AttachModalSession(ModalSession session) => ViewModel.AttachModalSession(session);
    }
}

