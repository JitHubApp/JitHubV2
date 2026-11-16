using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Identifies the font context whose x-height an inline image aligns to,
/// so one measure resolves each distinct style only once.
/// </summary>
internal readonly record struct InlineImageFontMetricKey(
    string FontFamily,
    float FontSize,
    ushort FontWeight,
    FontStyle FontStyle,
    FlowDirection FlowDirection,
    string Language,
    bool IsDark,
    bool IsHighContrast,
    double TextScaleFactor);
