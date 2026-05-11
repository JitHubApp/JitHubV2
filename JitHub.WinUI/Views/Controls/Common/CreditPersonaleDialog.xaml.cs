using JitHub.Models;
using System;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class CreditPersonaleDialog : UserControl
    {
        public CreditPersonale Person { get; }
        public ICommand CancelCommand { get; }

        public CreditPersonaleDialog(CreditPersonale person, ICommand cancelCommand)
        {
            Person = person ?? throw new ArgumentNullException(nameof(person));
            CancelCommand = cancelCommand ?? throw new ArgumentNullException(nameof(cancelCommand));
            this.InitializeComponent();
        }
    }
}


