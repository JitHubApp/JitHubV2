using System;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Identifies the part of the renderer environment that changed. The flags
/// deliberately separate paint-only state from values that affect text
/// shaping, geometry, or device-pixel placement.
/// </summary>
[Flags]
internal enum MarkdownEnvironmentChange
{
    None = 0,
    Brushes = 1 << 0,
    Typography = 1 << 1,
    Dpi = 1 << 2,
    TextScale = 1 << 3,
    Language = 1 << 4,
    FlowDirection = 1 << 5,
    Width = 1 << 6,

    Relayout = Typography | Dpi | TextScale | Language | FlowDirection | Width,
}

/// <summary>
/// Immutable, UI-object-free description of the effective rendering
/// environment. It is safe to retain with background layout work.
/// </summary>
internal readonly record struct MarkdownEnvironmentSnapshot(
    long Revision,
    long BrushRevision,
    long LayoutRevision,
    int Theme,
    bool IsHighContrast,
    double TextScaleFactor,
    string Language,
    int FlowDirection,
    double RasterizationScale,
    double Width,
    uint AccentColor,
    uint ForegroundColor,
    uint BackgroundColor)
{
    private const double ScaleTolerance = 0.0001;
    private const double WidthTolerance = 0.5;

    /// <summary>Returns the changes needed to move from <paramref name="previous"/> to this snapshot.</summary>
    public MarkdownEnvironmentChange ChangesFrom(in MarkdownEnvironmentSnapshot previous)
    {
        var changes = MarkdownEnvironmentChange.None;

        if (Theme != previous.Theme ||
            IsHighContrast != previous.IsHighContrast ||
            AccentColor != previous.AccentColor ||
            ForegroundColor != previous.ForegroundColor ||
            BackgroundColor != previous.BackgroundColor)
        {
            changes |= MarkdownEnvironmentChange.Brushes;
        }

        if (Math.Abs(TextScaleFactor - previous.TextScaleFactor) > ScaleTolerance)
            changes |= MarkdownEnvironmentChange.Typography | MarkdownEnvironmentChange.TextScale;

        if (!string.Equals(Language, previous.Language, StringComparison.OrdinalIgnoreCase))
            changes |= MarkdownEnvironmentChange.Typography | MarkdownEnvironmentChange.Language;

        if (FlowDirection != previous.FlowDirection)
            changes |= MarkdownEnvironmentChange.Typography | MarkdownEnvironmentChange.FlowDirection;

        if (Math.Abs(RasterizationScale - previous.RasterizationScale) > ScaleTolerance)
            changes |= MarkdownEnvironmentChange.Dpi;

        if (Math.Abs(Width - previous.Width) > WidthTolerance)
            changes |= MarkdownEnvironmentChange.Width;

        return changes;
    }

    /// <summary>True when the change can be satisfied without rebuilding geometry.</summary>
    public static bool IsBrushOnly(MarkdownEnvironmentChange changes)
        => changes != MarkdownEnvironmentChange.None &&
           (changes & MarkdownEnvironmentChange.Brushes) != 0 &&
           (changes & MarkdownEnvironmentChange.Relayout) == 0;

    /// <summary>Returns the same values with monitor-owned revision counters.</summary>
    public MarkdownEnvironmentSnapshot WithRevisions(long revision, long brushRevision, long layoutRevision)
        => this with
        {
            Revision = revision,
            BrushRevision = brushRevision,
            LayoutRevision = layoutRevision,
        };
}
