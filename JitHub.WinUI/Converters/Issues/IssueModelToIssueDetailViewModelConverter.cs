using JitHub.Models;
using JitHub.Models.Base;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Issues
{
    public partial class IssueModelToIssueDetailViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var issue = value as RepoSelectableItemModel<Issue>;
            return issue is not null
                ? new RepoIssueDetailViewModel(issue)
                : DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}



