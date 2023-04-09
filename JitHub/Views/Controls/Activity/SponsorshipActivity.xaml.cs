using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class SponsorshipActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SponsorshipActivityViewModel),
            typeof(SponsorshipActivity),
            new PropertyMetadata(default(SponsorshipActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SponsorshipActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public SponsorshipActivityViewModel ViewModel
        {
            get => (SponsorshipActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public SponsorshipActivity()
        {
            this.InitializeComponent();
        }
    }
}
