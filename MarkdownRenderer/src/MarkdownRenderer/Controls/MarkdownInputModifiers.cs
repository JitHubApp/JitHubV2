using System;

namespace MarkdownRenderer.Controls;

/// <summary>Keyboard modifiers captured for a markdown command or activation.</summary>
[Flags]
public enum MarkdownInputModifiers
{
    /// <summary>No modifier keys.</summary>
    None = 0,
    /// <summary>Control key.</summary>
    Control = 1,
    /// <summary>Shift key.</summary>
    Shift = 2,
    /// <summary>Alt/Menu key.</summary>
    Alt = 4,
    /// <summary>Windows key.</summary>
    Windows = 8,
}
