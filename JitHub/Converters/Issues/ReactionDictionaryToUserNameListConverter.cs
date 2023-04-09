using JitHub.Helpers;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Issues
{
    public class ReactionDictionaryToUserNameListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return null;
            var userReactions = (Dictionary<ReactionType, ICollection<string>>)value;
            var type = ((string)parameter).ToReactionType();
            return userReactions.GetValueOrDefault(type);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
