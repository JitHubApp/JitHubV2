using System;
using MarkdownRenderer.Images.Svg;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images.Placement;

/// <summary>
/// The observable result of an image that already presented in an earlier
/// layout of the same document. A deferred Mermaid or Math scene
/// publication rebuilds the whole document; without this record every fresh
/// image box whose pixels had been released offscreen reported Loading again
/// and reserved a placeholder size, so readers saw placeholders and reflowed
/// content for images that had already loaded. Only scalars and strings are
/// carried: never bitmaps, leases, bytes or resolver results, so a carried box
/// still resolves and decodes normally when it becomes visible.
/// </summary>
internal sealed record CompletedImageCarry(
    string Url,
    bool IsSvg,
    Size IntrinsicSize,
    string? SvgTitle,
    string? SvgDescription,
    bool Failed = false,
    MarkdownSvgFailureReason? SvgFailureReason = null,
    string? SvgFailureDescription = null)
{
    /// <summary>
    /// A permanent failure: the image already failed in an earlier
    /// layout and would fail again the same way. The rebuilt box shows the
    /// same failure placeholder instead of reporting Loading until retried.
    /// The control carries failures only across automatic scene rebuilds, so
    /// a user-requested rebuild still retries them.
    /// </summary>
    internal static CompletedImageCarry ForFailure(
        string url,
        bool isSvg,
        Size intrinsicSize,
        MarkdownSvgFailureReason? svgFailureReason,
        string? svgFailureDescription) =>
        new(url, isSvg, intrinsicSize, null, null, Failed: true, svgFailureReason, svgFailureDescription);

    internal bool HasUsableIntrinsicSize =>
        double.IsFinite(IntrinsicSize.Width) && double.IsFinite(IntrinsicSize.Height) &&
        IntrinsicSize.Width > 0 && IntrinsicSize.Height > 0;
}
