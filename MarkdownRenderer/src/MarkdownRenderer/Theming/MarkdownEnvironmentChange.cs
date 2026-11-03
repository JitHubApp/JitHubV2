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
