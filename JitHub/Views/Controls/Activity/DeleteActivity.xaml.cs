using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class DeleteActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(DeleteActivityViewModel),
            typeof(DeleteActivity),
            new PropertyMetadata(default(DeleteActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DeleteActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public DeleteActivityViewModel ViewModel
        {
            get => (DeleteActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public DeleteActivity()
        {
            this.InitializeComponent();
        }
    }
}
