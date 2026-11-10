using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A private raster placement is tried only for an opted-in session, a
/// bounded number of top-level blocks and image plans, and no hosted elements.
/// </summary>
public sealed class RasterPlacementEligibilityTests
{
    private const int Roots = LayoutSnapshot.MaximumPrivateRasterPlacementRoots;
    private const int Images = InlineContainerBox.MaximumPrivateRasterPlacementImages;

    [Theory]
    [InlineData(true, 1, 0, 1, true)]
    [InlineData(true, Roots, 0, Images, true)]
    [InlineData(false, 1, 0, 1, false)]
    [InlineData(true, 0, 0, 1, false)]
    [InlineData(true, Roots + 1, 0, 1, false)]
    [InlineData(true, 1, 1, 1, false)]
    [InlineData(true, 1, 0, 0, false)]
    [InlineData(true, 1, 0, Images + 1, false)]
    public void PrivatePlacementNeedsAnOptedInBoundedDocumentWithoutHostedElements(
        bool optedIn, int topLevelBlocks, int hostedElementPlans, int imagePlans, bool allowed)
    {
        Assert.Equal(allowed, RasterPlacementEligibility.Allows(optedIn, topLevelBlocks, hostedElementPlans, imagePlans));
    }
}
