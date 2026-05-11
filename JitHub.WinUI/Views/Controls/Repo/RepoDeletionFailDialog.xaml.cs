using JitHub.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Repo
{
    public sealed partial class RepoDeletionFailDialog : UserControl
    {
        public List<FailedRepo> FailedRepos { get; }
        public ICommand CancelCommand { get; }
        public RepoDeletionFailDialog(IEnumerable<FailedRepo> repos, ICommand cancel)
        {
            FailedRepos = repos.ToList();
            CancelCommand = cancel;
            this.InitializeComponent();
        }
    }
}


