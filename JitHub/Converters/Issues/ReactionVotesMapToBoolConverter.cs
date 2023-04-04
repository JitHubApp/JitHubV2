using JitHub.Helpers;
using Octokit;
using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Issues
{
    public class ReactionVotesMapToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return null;
            var votesMap = (Dictionary<ReactionType, Reaction>)value;
            var type = ((string)parameter).ToReactionType();
            return votesMap.ContainsKey(type);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
