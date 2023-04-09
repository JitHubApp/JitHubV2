using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Activities;

class PushEventPayloadToCommitStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        ICollection<Octokit.Commit> commits = (ICollection<Octokit.Commit>)value;
        return $"{commits.Count} commits to";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
