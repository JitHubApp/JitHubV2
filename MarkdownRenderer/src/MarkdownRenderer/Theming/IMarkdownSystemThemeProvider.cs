using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkdownRenderer.Theming;

internal interface IMarkdownSystemThemeProvider
{
    bool IsHighContrast { get; }
    Color WindowTextColor { get; }
    Color WindowColor { get; }
    Color HotlightColor { get; }
    Color HighlightColor { get; }
    Color HighlightTextColor { get; }
}
