using JitHub.ViewModels.CommitViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Commit
{
    public sealed partial class CommitDetail : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(CommitDetailViewModel),
            typeof(CommitDetail),
            new PropertyMetadata(default(CommitDetailViewModel), OnViewModelChanged)
        );

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CommitDetail self && e.NewValue != null)
            {
                self.DataContext = e.NewValue;
                var viewModel = self.DataContext as CommitDetailViewModel;
                viewModel.LoadCommand.Execute(null);
            }
        }

        public CommitDetailViewModel ViewModel
        {
            get => (CommitDetailViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public CommitDetail()
        {
            this.InitializeComponent();
        }
    }
}
