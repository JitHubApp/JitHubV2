using System.Collections.Generic;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// The DIP size and DPI of one selection-adornment surface allocation.
/// </summary>
internal readonly record struct SelectionAdornerSurfaceAllocation(int Width, int Height, float Dpi);
