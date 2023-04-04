using JitHub.Models;
using JitHub.Models.Base;
using JitHub.ViewModels.PullRequestViewModels;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.PullRequests
{
    class PullRequestModelToPullRequestDetailViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var pullRequest = value as RepoSelectableItemModel<PullRequest>;
            return pullRequest != null ? new RepoPullRequestDetailViewModel(pullRequest) : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
