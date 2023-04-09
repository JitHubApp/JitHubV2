using JitHub.ViewModels.PullRequestViewModels.ConversationViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest.Conversation
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
            if (d is ReviewCommentBlock self && args.NewValue != null)
            {
                self.ViewModel = (ReviewCommentViewModel)args.NewValue;
                self.DataContext = self.ViewModel;
                self.ViewModel.ReplyBox = self.ReplyBox;
            }
        }

        public ReviewCommentViewModel ViewModel
        {
            get => (ReviewCommentViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }
        public ReviewCommentBlock()
        {
            this.InitializeComponent();
        }
    }
}
