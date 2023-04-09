using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class PublicActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(PublicActivityViewModel),
            typeof(PublicActivity),
            new PropertyMetadata(default(PublicActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PublicActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public PublicActivityViewModel ViewModel
        {
            get => (PublicActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public PublicActivity()
        {
            this.InitializeComponent();
        }
    }
}
