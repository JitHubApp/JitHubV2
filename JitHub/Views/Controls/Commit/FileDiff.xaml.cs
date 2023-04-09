using JitHub.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Commit
{
    public sealed partial class FileDiff : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(FileDiffViewModel),
            typeof(FileDiff),
            new PropertyMetadata(default(FileDiffViewModel), OnViewModelChanged)
        );

        public static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileDiff self && e.NewValue != null)
            {
                self.DataContext = (FileDiffViewModel)e.NewValue;
            }
        }

        public FileDiffViewModel ViewModel
        {
            get => (FileDiffViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public FileDiff()
        {
            this.InitializeComponent();
        }
    }
}
