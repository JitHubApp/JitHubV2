using JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest.Conversation
{
    public sealed partial class ReviewCommentBlock : UserControl
    {
        public static DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ReviewCommentViewModel),
            typeof(ReviewCommentBlock),
            new PropertyMetadata(null, OnViewModelChange)
        );

        public static void OnViewModelChange(DependencyObject d, DependencyPropertyChangedEventArgs args)
        {
            if (d is not ReviewCommentBlock self)
            {
                return;
            }

            if (args.OldValue is ReviewCommentViewModel oldViewModel)
            {
                oldViewModel.ReplyBoxRequested -= self.OnReplyBoxRequested;
            }

            if (args.NewValue is ReviewCommentViewModel viewModel)
            {
                self.DataContext = viewModel;
                viewModel.ReplyBoxRequested += self.OnReplyBoxRequested;
            }
            else
            {
                self.DataContext = null;
                self.ReplyMarkdownForm.AutomationInstanceId = null;
            }

            self.Bindings.Update();
        }

        private void OnReplyBoxRequested(object? sender, EventArgs e)
        {
            ReplyBox.IsExpanded = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                ReplyBox.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.2
                });
                ReplyMarkdownForm.Focus(FocusState.Programmatic);
            });
        }

        public ReviewCommentViewModel? ViewModel
        {
            get => (ReviewCommentViewModel?)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public ReviewCommentBlock()
        {
            this.InitializeComponent();
        }
    }
}

