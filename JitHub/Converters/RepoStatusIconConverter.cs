using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters
{
    class RepoStatusIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var repo = value as Repository;
            if (repo.Fork)
            {
                return "ms-appx:///Assets/fork_repo_icon.svg";
            }
            else if (repo.Private)
            {
                return "ms-appx:///Assets/private_repo_icon.svg";
            }
            else
            {
                return "ms-appx:///Assets/public_repo_icon.svg";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
