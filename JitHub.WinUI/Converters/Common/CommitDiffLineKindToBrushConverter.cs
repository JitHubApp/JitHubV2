using System;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Converters.Common;

public sealed partial class CommitDiffLineKindToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        CommitDiffLineKind kind = value is CommitDiffLineKind typedKind
            ? typedKind
            : CommitDiffLineKind.Context;
        string token = parameter as string ?? "foreground";
        string resourceKey = token switch
        {
            "background" => kind switch
            {
                CommitDiffLineKind.Addition => "SystemFillColorSuccessBackgroundBrush",
                CommitDiffLineKind.Deletion => "SystemFillColorCriticalBackgroundBrush",
                CommitDiffLineKind.Hunk => "AppCanvasRaisedBrush",
                CommitDiffLineKind.Binary => "AppCanvasRaisedBrush",
                _ => "AppCanvasInsetBrush"
            },
            "foreground" => kind switch
            {
                CommitDiffLineKind.Addition => "AppSuccessBrush",
                CommitDiffLineKind.Deletion => "AppDangerBrush",
                CommitDiffLineKind.Hunk => "AppAccentBrush",
                CommitDiffLineKind.Binary => "AppInkMutedBrush",
                CommitDiffLineKind.NoNewline => "AppInkMutedBrush",
                _ => "AppInkBrush"
            },
            _ => "AppInkBrush"
        };

        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush
            ? brush
            : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
