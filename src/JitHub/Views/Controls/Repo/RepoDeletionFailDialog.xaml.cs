using JitHub.Models;
using System.Collections.Generic;
using System.Windows.Input;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Repo
{
    public sealed partial class RepoDeletionFailDialog : UserControl
    {
        public ICollection<FailedRepo> FailedRepos { get; }
        public ICommand CancelCommand { get; }
        public RepoDeletionFailDialog(ICollection<FailedRepo> repos, ICommand cancel)
        {
            this.InitializeComponent();
            FailedRepos = repos;
            CancelCommand = cancel;
        }
    }
}
