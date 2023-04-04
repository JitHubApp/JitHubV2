using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Activity
{
    public sealed partial class PullRequestReviewCommentActivity : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(PullRequestCommentActivityViewModel),
            typeof(PullRequestReviewCommentActivity),
            new PropertyMetadata(default(PullRequestCommentActivityViewModel), null)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PullRequestReviewCommentActivity self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public PullRequestCommentActivityViewModel ViewModel
        {
            get => (PullRequestCommentActivityViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public PullRequestReviewCommentActivity()
        {
            this.InitializeComponent();
        }
    }
}
