using JitHub.Models;
using JitHub.ViewModels.CommitViewModels;
using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Commit
{
    public class CommandableCommitToDetailViewModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var commit = value as CommandableCommit;
            return commit != null ? new CommitDetailViewModel(commit) : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
