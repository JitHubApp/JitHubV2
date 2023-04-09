using JitHub.Models;
using Octokit;
using System;
using System.Linq;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters
{
    class RepoContentNodeToMarkDownConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;
            var node = value as RepoContentNode;
            var splits = node.Name.Split(".");
            var format = splits.Last();
            if (format.ToLower() == "md") return node.Content;
            var code = String.Format("```{0}\n{1}\n```", format, node.Content);
            return code;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
