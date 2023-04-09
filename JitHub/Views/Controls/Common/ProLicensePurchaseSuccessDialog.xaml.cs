using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class ProLicensePurchaseSuccessDialog : UserControl
    {
        public ICommand ConfirmCommand { get; }
        public ProLicensePurchaseSuccessDialog(ICommand command)
        {
            this.InitializeComponent();
            ConfirmCommand = command;
        }
    }
}
