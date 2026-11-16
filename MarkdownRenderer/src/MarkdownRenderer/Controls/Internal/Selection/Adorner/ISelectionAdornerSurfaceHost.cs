using System.Collections.Generic;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// The XAML property that displays a selection-adornment surface.
/// </summary>
internal interface ISelectionAdornerSurfaceHost<TSurface>
    where TSurface : class
{
    TSurface? Source { get; set; }
}
