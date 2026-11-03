using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// State of one private raster-candidate build, from capture under the source
/// gate to the private snapshot measured outside it. Whatever it still owns
/// when the build ends unpublished (roots or the snapshot) is released into
/// <see cref="Context"/>.
/// </summary>
internal sealed class PrivateRasterCandidateBuild
{
    internal PrivateRasterCandidateBuild(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
    }

    internal CancellationToken CancellationToken { get; }

    internal MarkdownLayoutContext SourceContext { get; set; } = null!;

    internal HashSet<int> ChangedOrdinals { get; set; } = null!;

    internal bool LazyLayoutEnabled { get; set; }

    internal bool[]? MeasuredRootMask { get; set; }

    internal float[]? CapturedRootHeights { get; set; }

    internal int CapturedMeasuredRootCount { get; set; }

    internal SharedLayoutMetrics? CapturedSharedLayoutMetrics { get; set; }

    internal long Revision { get; set; }

    /// <summary>Fresh raster-placement context that owns every private root.</summary>
    internal MarkdownLayoutContext? Context { get; set; }

    internal float CapturedContentWidth { get; set; }

    internal int ImageCount { get; set; }

    internal HorizontalPlacementSourceFence[]? HorizontalSources { get; set; }

    internal MarkdownSemanticTextProjectionReuse? TextProjectionReuse { get; set; }

    /// <summary>Private roots; the sole cleanup owner until the snapshot adopts them.</summary>
    internal List<BlockBox>? Roots { get; set; }

    internal List<RasterPlacementSourceFence>? Sources { get; set; }

    internal List<CodeBlockPlacementSourceFence>? CodeSources { get; set; }

    /// <summary>Private snapshot; retired into the context unless published.</summary>
    internal LayoutSnapshot? Snapshot { get; set; }
}
