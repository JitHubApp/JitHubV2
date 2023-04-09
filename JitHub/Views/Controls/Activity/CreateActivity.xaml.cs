using JitHub.ViewModels.ActivityViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class CreateActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(CreateActivityViewModel),
            typeof(CreateActivity),
            new PropertyMetadata(default(CreateActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CreateActivity self && e.NewValue != null)
            {
                self.ViewModel = e.NewValue as CreateActivityViewModel;
                self.DataContext = self.ViewModel;
            }
        }

        public CreateActivityViewModel ViewModel
        {
            get => (CreateActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public CreateActivity()
        {
            this.InitializeComponent();
        }
    }
}
