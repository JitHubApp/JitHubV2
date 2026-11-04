using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Assigns each SVG renderer and raster decoder a stable positive number for
/// cache keys. The number is held weakly with the provider, so a collected
/// provider never keeps a cache entry reachable and a new provider never
/// reuses an older provider's number.
/// </summary>
internal sealed class ImageProviderIdentities
{
    private readonly ConditionalWeakTable<IMarkdownSvgRenderer, Identity> _svgRenderers = new();
    private readonly ConditionalWeakTable<IMarkdownRasterDecoder, Identity> _rasterDecoders = new();
    private readonly ConditionalWeakTable<IMarkdownSvgRenderer, Identity>.CreateValueCallback _createSvgRenderer;
    private readonly ConditionalWeakTable<IMarkdownRasterDecoder, Identity>.CreateValueCallback _createRasterDecoder;
    private long _nextSvgRenderer;
    private long _nextRasterDecoder;

    internal ImageProviderIdentities()
    {
        _createSvgRenderer = _ => new Identity(Interlocked.Increment(ref _nextSvgRenderer));
        _createRasterDecoder = _ => new Identity(Interlocked.Increment(ref _nextRasterDecoder));
    }

    internal long GetSvgRendererIdentity(IMarkdownSvgRenderer renderer) =>
        _svgRenderers.GetValue(renderer, _createSvgRenderer).Value;

    internal long GetRasterDecoderIdentity(IMarkdownRasterDecoder decoder) =>
        _rasterDecoders.GetValue(decoder, _createRasterDecoder).Value;

    private sealed class Identity(long value)
    {
        internal readonly long Value = value;
    }
}
