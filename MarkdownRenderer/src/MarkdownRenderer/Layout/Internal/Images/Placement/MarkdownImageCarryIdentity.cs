using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.Images.Placement;

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
