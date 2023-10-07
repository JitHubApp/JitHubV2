using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class ForkActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ForkActivityViewModel),
            typeof(ForkActivity),
            new PropertyMetadata(default(ForkActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ForkActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public ForkActivityViewModel ViewModel
        {
            get => (ForkActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public ForkActivity()
        {
            this.InitializeComponent();
        }
    }
}
