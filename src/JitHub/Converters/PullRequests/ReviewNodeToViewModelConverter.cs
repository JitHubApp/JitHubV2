using JitHub.Models.PRConversation;
using JitHub.ViewModels.PullRequestViewModels.ConversationViewModels;
using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.PullRequests
{
    public class ReviewNodeToViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null || !(value is ReviewNode)) return new Exception("Failed to recognize ReviewNode object");
            var reviewNode = (ReviewNode)value;
            return new ReviewNodeViewModel(reviewNode);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
