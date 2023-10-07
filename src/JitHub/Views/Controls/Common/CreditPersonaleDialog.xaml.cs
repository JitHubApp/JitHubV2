using JitHub.Models;
using System.Windows.Input;
using Windows.UI;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class CreditPersonaleDialog : UserControl
    {
        public CreditPersonale Person { get; set; }
        public ICommand CancelCommand { get; set; }
        public CreditPersonaleDialog(CreditPersonale person, ICommand cancelCommand)
        {
            this.InitializeComponent();
            Person = person;
            CancelCommand = cancelCommand;
        }
    }
}
