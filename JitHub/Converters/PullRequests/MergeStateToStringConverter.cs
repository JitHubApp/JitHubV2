using Octokit;
using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.PullRequests
{
    public class MergeStateToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;
            StringEnum<MergeableState> state = (StringEnum<MergeableState>)value;
            switch (state.Value)
            {
                case MergeableState.Behind:
                    return "✔ Some commits are pending/failing.";
                case MergeableState.Blocked:
                    return "❌ There's an issue that prevents merging";
                case MergeableState.Clean:
                    return "✔ This branch has no conflicts with the base branch";
                case MergeableState.Dirty:
                    return "❌ Merge conflicts. Please resolve.";
                case MergeableState.HasHooks:
                    return "❌ System is running pre-commit checks.";
                case MergeableState.Unknown:
                    return "❌ Mergeability was not checked yet.";
                case MergeableState.Unstable:
                    return "❌ Failing/missing required status check.";
                default:
                    return "✔ Some commits are pending/failing.";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
