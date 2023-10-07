using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class WatchActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(WatchActivityViewModel),
            typeof(WatchActivity),
            new PropertyMetadata(default(WatchActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WatchActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public WatchActivityViewModel ViewModel
        {
            get => (WatchActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public WatchActivity()
        {
            this.InitializeComponent();
        }
    }
}
