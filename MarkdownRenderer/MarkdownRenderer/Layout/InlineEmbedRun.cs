using System;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Inline run that reserves a fixed-size cell within a paragraph or heading
/// for a hosted WinUI element (e.g. a CheckBox for a GFM task list item).
/// The cell is filled by a transparent <c>U+FFFC</c> object replacement
/// character whose advance width is forced by
/// <c>CanvasTextLayout.SetCharacterSpacing</c>.
/// </summary>
public sealed class InlineEmbedRun : InlineRun
{
    public const string PlaceholderChar = "\uFFFC";

    public float DesiredWidth { get; }
    public float DesiredHeight { get; }

    /// <summary>
    /// UI-thread factory invoked when the embed is realized. Closures may
    /// capture primitives (e.g. <c>Checked</c>) but MUST NOT capture
    /// FrameworkElements created on a different thread.
    /// </summary>
    public Func<FrameworkElement> ElementFactory { get; }

    /// <summary>
    /// Set by <see cref="MarkdownRenderer.Layout.Boxes.InlineContainerBox"/>
    /// once the element is realized on the UI thread.
    /// </summary>
    public FrameworkElement? RealizedElement { get; set; }

    public InlineEmbedRun(float desiredWidth, float desiredHeight, Func<FrameworkElement> factory)
    {
        DesiredWidth = Math.Max(1f, desiredWidth);
        DesiredHeight = Math.Max(1f, desiredHeight);
        ElementFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        RenderedLength = 1;
    }

    public override string Text => PlaceholderChar;
}
