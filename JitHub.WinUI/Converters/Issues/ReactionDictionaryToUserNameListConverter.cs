using JitHub.WinUI.Helpers;
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
    public partial class ReactionDictionaryToUserNameListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not Dictionary<ReactionType, ICollection<string>> userReactions || parameter is not string reactionName)
            {
                return new List<string>();
            }

            var type = reactionName.ToReactionType();
            return userReactions.GetValueOrDefault(type)?.ToList() ?? new List<string>();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

