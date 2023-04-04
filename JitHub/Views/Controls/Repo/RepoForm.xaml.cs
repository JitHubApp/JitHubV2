using System.Windows.Input;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Repo
{
    public sealed partial class RepoForm : UserControl
    {
        public RepoForm(ICommand refreshcommand)
        {
            this.InitializeComponent();
            ViewModel.Init(refreshcommand);
        }
    }
}
