using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Common
{
    public class ReactionTypeToEmojiConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null || !(value is ReactionType)) return null;
            var reaction = (ReactionType)value;
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
                    return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
