using JitHub.Models;
using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.ViewModels.IssueViewModels;
using JitHub.ViewModels.UserViewModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.PullRequests
{
    public class IssueCommentNodeToUserCommentBlockViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null || (!(value is IssueCommentNode) && !(value is UserCommentBlockViewModel))) throw new ArgumentNullException(nameof(value));
            if (value is UserCommentBlockViewModel) return value as UserCommentBlockViewModel; // This is a hack because somehow this converter is called twitce
            var comment = (IssueCommentNode)value;
            return new UserCommentBlockViewModel(comment);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
