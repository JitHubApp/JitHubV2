using System.Windows.Input;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class FeaturePurchaseDialog : UserControl
    {
        public ICommand BuyCommand { get; }
        public ICommand CancelCommand { get; }
        public FeaturePurchaseDialog(ICommand buyCommand, ICommand cancelCommand)
        {
            this.InitializeComponent();
            BuyCommand = buyCommand;
            CancelCommand = cancelCommand;
        }
    }
}
