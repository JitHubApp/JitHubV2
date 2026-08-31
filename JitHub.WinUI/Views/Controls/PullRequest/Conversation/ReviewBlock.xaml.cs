using JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest.Conversation
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
            if (d is not ReviewBlock self)
                return;

            if (e.NewValue is ReviewNodeViewModel viewModel)
            {
                self.DataContext = viewModel;
                self.Bindings.Update();
                self.ReviewerAvatar.Login = viewModel.AuthenticatedReviewerLogin;
                self.ReviewerAvatar.AutomationInstanceId = viewModel.ReviewerAutomationId;
            }
            else
            {
                self.DataContext = null;
                self.ReviewerAvatar.Login = null;
                self.ReviewerAvatar.AutomationInstanceId = "PullRequestReview_unknown";
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

