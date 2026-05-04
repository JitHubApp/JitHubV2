using System;
using JitHub.Models.Activities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Converters.Activities;

public sealed partial class ActivityToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        ActivityCardTone tone = value is ActivityCardTone activityTone ? activityTone : ActivityCardTone.Neutral;
        string mode = parameter as string ?? "Foreground";
        string key = mode.Equals("Border", StringComparison.OrdinalIgnoreCase)
            ? BorderKey(tone)
            : ForegroundKey(tone);

        return Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush
            ? brush
            : Application.Current.Resources["AppInkMutedBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    private static string ForegroundKey(ActivityCardTone tone) => tone switch
    {
        ActivityCardTone.Accent => "AppAccentBrush",
        ActivityCardTone.Success => "AppSuccessBrush",
        ActivityCardTone.Warning => "AppWarmAccentBrush",
        ActivityCardTone.Danger => "AppDangerBrush",
        ActivityCardTone.Gold => "AppWarmAccentBrush",
        ActivityCardTone.Purple => "AppAccentBrush",
        _ => "AppInkMutedBrush"
    };

    private static string BorderKey(ActivityCardTone tone) => tone switch
    {
        ActivityCardTone.Neutral => "AppOutlineBrush",
        _ => ForegroundKey(tone)
    };
}
