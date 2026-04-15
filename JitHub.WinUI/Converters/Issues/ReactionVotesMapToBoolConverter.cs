using JitHub.WinUI.Helpers;
using Octokit;
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Issues
{
    public partial class ReactionVotesMapToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not Dictionary<ReactionType, Reaction> votesMap || parameter is not string reactionName)
            {
                return false;
            }

            var type = reactionName.ToReactionType();
            return votesMap.ContainsKey(type);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

