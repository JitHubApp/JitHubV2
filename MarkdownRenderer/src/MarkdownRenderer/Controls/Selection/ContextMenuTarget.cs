using System;
using System.Threading;

namespace MarkdownRenderer.Controls.Selection;

internal sealed class ContextMenuTarget : IDisposable
{
    private Layout.Boxes.SharedCanvasBitmapCache.Lease? _imageBitmapLease;

    public string? LinkUrl { get; set; }
    public SourceSpan LinkSourceRange { get; set; }
    public string? ImageUrl { get; set; }
    public SourceSpan ImageSourceRange { get; set; }
    public string? CodeText { get; set; }
    public string? CodeLanguage { get; set; }
    public SourceSpan CodeSourceRange { get; set; }
    public MarkdownTableClipboardSnapshot? TableSnapshot { get; set; }
    public SourceSpan TableSourceRange { get; set; }

    public Layout.Boxes.SharedCanvasBitmapCache.Lease? ImageBitmapLease =>
        Volatile.Read(ref _imageBitmapLease);

    public void SetImageBitmapLease(Layout.Boxes.SharedCanvasBitmapCache.Lease? lease)
    {
        Layout.Boxes.SharedCanvasBitmapCache.Lease? previous =
            Interlocked.Exchange(ref _imageBitmapLease, lease);
        previous?.Dispose();
    }

    public ContextMenuTarget CloneForExecution() => new()
    {
        LinkUrl = LinkUrl,
        LinkSourceRange = LinkSourceRange,
        ImageUrl = ImageUrl,
        ImageSourceRange = ImageSourceRange,
        CodeText = CodeText,
        CodeLanguage = CodeLanguage,
        CodeSourceRange = CodeSourceRange,
        TableSnapshot = TableSnapshot,
        TableSourceRange = TableSourceRange,
        _imageBitmapLease = ImageBitmapLease?.TryClone(),
    };

    public void Dispose() =>
        Interlocked.Exchange(ref _imageBitmapLease, null)?.Dispose();
}
