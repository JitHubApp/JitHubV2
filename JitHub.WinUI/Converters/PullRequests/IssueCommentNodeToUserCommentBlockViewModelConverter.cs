using JitHub.Models;
using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.ViewModels.UserViewModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.PullRequests
{
    public partial class IssueCommentNodeToUserCommentBlockViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is UserCommentBlockViewModel viewModel)
            {
                return viewModel;
            }

            if (value is not IssueCommentNode comment)
            {
                return DependencyProperty.UnsetValue;
            }

            return new UserCommentBlockViewModel(comment);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}



