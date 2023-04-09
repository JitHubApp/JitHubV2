using JitHub.ViewModels.PullRequestViewModels.ConversationViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest.Conversation
{
    public sealed partial class ReviewBlock : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ReviewNodeViewModel),
            typeof(ReviewBlock),
            new PropertyMetadata(default(ReviewNodeViewModel), OnViewModelChange));

        private static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ReviewBlock self && e.NewValue != null)
            {
                self.DataContext = self.ViewModel;
            }
        }

        public ReviewNodeViewModel ViewModel
        {
            get => (ReviewNodeViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public ReviewBlock()
        {
            this.InitializeComponent();
        }
    }
}
