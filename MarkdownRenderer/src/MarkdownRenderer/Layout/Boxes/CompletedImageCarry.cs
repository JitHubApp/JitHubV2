using System;
using MarkdownRenderer.Images;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

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

/// <summary>
/// Everything that decides what an image URL resolves to. A carried image
/// result is valid only for a rebuild of the same source text with the same
/// image configuration; any change discards every carried result.
/// </summary>
internal sealed class MarkdownImageCarryIdentity(
    string sourceText,
    Uri? imageBaseUri,
    string? imageDocumentPath,
    MarkdownDocumentSource? imageDocumentSource,
    object? imageResolver,
    object? svgRenderer,
    object? rasterDecoder,
    bool allowThirdPartyRemoteImages)
{
    private readonly string _sourceText = sourceText ?? string.Empty;
    private readonly Uri? _imageBaseUri = imageBaseUri;
    private readonly string? _imageDocumentPath = imageDocumentPath;
    private readonly MarkdownDocumentSource? _imageDocumentSource = imageDocumentSource;
    private readonly object? _imageResolver = imageResolver;
    private readonly object? _svgRenderer = svgRenderer;
    private readonly object? _rasterDecoder = rasterDecoder;
    private readonly bool _allowThirdPartyRemoteImages = allowThirdPartyRemoteImages;

    internal bool Matches(MarkdownImageCarryIdentity? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (ReferenceEquals(_imageResolver, other._imageResolver) &&
          ReferenceEquals(_svgRenderer, other._svgRenderer) &&
          ReferenceEquals(_rasterDecoder, other._rasterDecoder) &&
          _allowThirdPartyRemoteImages == other._allowThirdPartyRemoteImages &&
          Equals(_imageBaseUri, other._imageBaseUri) &&
          string.Equals(_imageDocumentPath, other._imageDocumentPath, StringComparison.Ordinal) &&
          Equals(_imageDocumentSource, other._imageDocumentSource) &&
          string.Equals(_sourceText, other._sourceText, StringComparison.Ordinal)));
}
