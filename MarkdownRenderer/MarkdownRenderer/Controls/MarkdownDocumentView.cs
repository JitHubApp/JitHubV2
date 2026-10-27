namespace MarkdownRenderer.Controls;

/// <summary>
/// Renders a markdown document inside an ancestor-owned viewport.
/// </summary>
/// <remarks>
/// This control does not create a <c>ScrollViewer</c>. It observes its effective
/// viewport so large-document layout, hosted-element realization, image loading,
/// syntax highlighting, and selection overlays follow the page or workspace
/// scroll surface that already owns navigation.
/// </remarks>
public sealed partial class MarkdownDocumentView : MarkdownRendererControl
{
    /// <summary>Initializes a non-scrolling markdown document view.</summary>
    public MarkdownDocumentView()
        : base(ownsScrollViewport: false)
    {
        Engine = MarkdownEngine.Default;
    }
}
