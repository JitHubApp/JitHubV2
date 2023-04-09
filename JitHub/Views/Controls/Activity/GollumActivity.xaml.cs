using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class GollumActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(GollumActivityViewModel),
            typeof(GollumActivity),
            new PropertyMetadata(default(GollumActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GollumActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public GollumActivityViewModel ViewModel
        {
            get => (GollumActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public GollumActivity()
        {
            this.InitializeComponent();
        }
    }
}
