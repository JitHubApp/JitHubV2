using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class PullRequestActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(PullRequestActivityViewModel),
            typeof(PullRequestActivity),
            new PropertyMetadata(default(PullRequestActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PullRequestActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public PullRequestActivityViewModel ViewModel
        {
            get => (PullRequestActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public PullRequestActivity()
        {
            this.InitializeComponent();
        }
    }
}
