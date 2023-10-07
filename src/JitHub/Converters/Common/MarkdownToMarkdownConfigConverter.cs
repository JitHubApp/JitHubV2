using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Common
{
    internal class MarkdownToMarkdownConfigConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var githubService = Ioc.Default.GetService<IGitHubService>();
            var markdown = value as string;
            var markdownConfig = githubService.GetMarkdownConfig(markdown);
            return markdownConfig;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
