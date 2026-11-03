namespace MarkdownRenderer.Controls;

/// <summary>
/// Convenience markdown viewer that owns its vertical scroll viewport.
/// </summary>
// Partial because the CsWinRT AOT generator emits this class's WinRT vtable.
public sealed partial class MarkdownScrollView : MarkdownRendererControl
{
    /// <summary>Initializes a scrolling markdown document view.</summary>
    public MarkdownScrollView()
        : base(ownsScrollViewport: true)
    {
        Engine = MarkdownEngine.Default;
    }
}
