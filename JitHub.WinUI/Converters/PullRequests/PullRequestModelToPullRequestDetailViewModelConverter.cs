using JitHub.Models;
using JitHub.Models.Base;
using JitHub.WinUI.ViewModels.PullRequestViewModels;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.PullRequests
{
    public partial class PullRequestModelToPullRequestDetailViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var pullRequest = value as RepoSelectableItemModel<PullRequest>;
            return pullRequest is not null
                ? new RepoPullRequestDetailViewModel(pullRequest)
                : DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}



