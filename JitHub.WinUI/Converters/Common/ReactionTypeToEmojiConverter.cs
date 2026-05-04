using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Common
{
    public partial class ReactionTypeToEmojiConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not ReactionType reaction)
            {
                return string.Empty;
            }

            switch (reaction)
            {
                case ReactionType.Plus1:
                    return "👍";
                case ReactionType.Minus1:
                    return "👎";
                case ReactionType.Laugh:
                    return "😀";
                case ReactionType.Hooray:
                    return "🎉";
                case ReactionType.Confused:
                    return "😕";
                case ReactionType.Heart:
                    return "❤️";
                case ReactionType.Rocket:
                    return "🚀";
                case ReactionType.Eyes:
                    return "👀";
                default:
                    return string.Empty;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

