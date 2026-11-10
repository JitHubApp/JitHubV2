using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

/// <summary>
/// The shared text-format leases one <see cref="CodeBlockBox"/> paints its
/// header label and gutter line numbers with. Each lease is acquired on the
/// first paint that draws its text and held until the box is disposed.
/// </summary>
/// <remarks>
/// Formats are shared immutable descriptors owned by
/// <see cref="SharedCanvasTextFormatCache"/>; the box releases its leases
/// rather than disposing the native formats.
/// </remarks>
internal sealed class CodeBlockTextFormatLeases
{
    private readonly MarkdownLayoutContext _context;
    private SharedCanvasTextFormatCache.Lease? _header;
    private SharedCanvasTextFormatCache.Lease? _lineNumber;

    /// <summary>Creates the empty lease set of one code block.</summary>
    /// <param name="context">The code block's layout context.</param>
    internal CodeBlockTextFormatLeases(MarkdownLayoutContext context)
    {
        _context = context;
    }

    /// <summary>The header-label lease, or null before the header text was first painted or after release.</summary>
    internal SharedCanvasTextFormatCache.Lease? HeaderLease => _header;

    /// <summary>The gutter line-number lease, or null before line numbers were first painted or after release.</summary>
    internal SharedCanvasTextFormatCache.Lease? LineNumberLease => _lineNumber;

    /// <summary>Returns the header-label format, acquiring its lease on first use.</summary>
    /// <param name="style">The resolved code-block language style.</param>
    /// <returns>The shared native format.</returns>
    internal CanvasTextFormat GetHeaderFormat(ElementStyle style)
    {
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        return (_header ??= _context.RenderResources.TextFormats.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            rtl ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left,
            _context.FlowDirection,
            _context.Language,
            useContentAwareLineSpacing: true,
            verticalAlignment: CanvasVerticalAlignment.Center)).Format;
    }

    /// <summary>Returns the gutter line-number format, acquiring its lease on first use.</summary>
    /// <param name="style">The resolved line-number style.</param>
    /// <returns>The shared native format.</returns>
    internal CanvasTextFormat GetLineNumberFormat(ElementStyle style)
        => (_lineNumber ??= _context.RenderResources.TextFormats.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            CanvasHorizontalAlignment.Right,
            FlowDirection.LeftToRight,
            _context.Language,
            useContentAwareLineSpacing: true,
            verticalAlignment: CanvasVerticalAlignment.Top)).Format;

    /// <summary>
    /// Releases the header lease, then the line-number lease, recording any
    /// failure in <paramref name="cleanup"/> so the caller still disposes the
    /// rest of the box.
    /// </summary>
    /// <param name="cleanup">The owning box's dispose scope.</param>
    internal void Release(ref BlockBoxCleanupScope cleanup)
    {
        cleanup.TryDisposeAndClear(ref _header);
        cleanup.TryDisposeAndClear(ref _lineNumber);
    }
}
