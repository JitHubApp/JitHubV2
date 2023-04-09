using JitHub.Models;
using JitHub.Models.Base;
using JitHub.ViewModels.IssueViewModels;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Issues
{
    class IssueModelToIssueDetailViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var issue = value as RepoSelectableItemModel<Issue>;
            return issue != null ? new RepoIssueDetailViewModel(issue) : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
