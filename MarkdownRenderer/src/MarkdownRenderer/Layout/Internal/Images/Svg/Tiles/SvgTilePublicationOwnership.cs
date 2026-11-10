using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// Coordinates ownership between accepted publication and its rejection or
/// exception cleanup callback. Once transferred, only the image or tile
/// dictionary owner may release the lease.
/// </summary>
internal struct SvgTilePublicationOwnership
{
    private int _state;

    internal bool TryTransfer() =>
        Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal bool TryReleaseUntransferred() =>
        Interlocked.CompareExchange(ref _state, 2, 0) == 0;
}
