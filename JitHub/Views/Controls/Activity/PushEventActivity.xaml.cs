using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class PushEventActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(PushActivityViewModel),
            typeof(PushEventActivity),
            new PropertyMetadata(default(PushActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PushEventActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public PushActivityViewModel ViewModel
        {
            get => (PushActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public PushEventActivity()
        {
            this.InitializeComponent();
        }
    }
}
