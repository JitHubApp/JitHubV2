using System;

namespace MarkdownRenderer.Extensions;

/// <summary>Behavioral flags carried by a vector semantic item.</summary>
[Flags]
public enum MarkdownVectorSemanticFlags
{
    /// <summary>No behavioral flags.</summary>
    None = 0,
    /// <summary>The item can receive logical focus.</summary>
    Focusable = 1 << 0,
    /// <summary>The item can participate in diagram selection.</summary>
    Selectable = 1 << 1,
    /// <summary>The item has a declarative link or action.</summary>
    Linked = 1 << 2,
    /// <summary>The item is excluded from accessible content.</summary>
    Decorative = 1 << 3,
}
