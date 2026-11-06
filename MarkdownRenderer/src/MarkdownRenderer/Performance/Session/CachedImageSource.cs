using System.Collections.Generic;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

/// <summary>One source-cache entry. Mutable fields are guarded by the owning cache's lock.</summary>
internal sealed class CachedImageSource(
    ImageSourceKey key,
    MarkdownImageAsset asset,
    long metadataWeight,
    long identity)
{
    internal const long RasterIntrinsicMetadataWeightBytes = 16;
    internal readonly ImageSourceKey Key = key;
    internal readonly MarkdownImageAsset Asset = asset;
    internal long MetadataWeight = metadataWeight;
    internal readonly long Identity = identity;
    internal RasterIntrinsicMetadata? RasterIntrinsicMetadata;
    internal LinkedListNode<CachedImageSource> Node = null!;
}
