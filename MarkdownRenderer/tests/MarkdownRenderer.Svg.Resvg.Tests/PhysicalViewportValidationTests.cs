using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class PhysicalViewportValidationTests
{
    [Theory]
    [InlineData(1112.5, 197.5)]
    [InlineData(1113, 198)]
    [InlineData(1112.0001, 197.0001)]
    public void AdmissionAcceptsFinitePositiveViewportWhoseCeilingMatchesAllocation(
        double viewportWidth,
        double viewportHeight)
    {
        var request = new MarkdownSvgRenderRequest(1113, 198)
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(viewportWidth, viewportHeight),
        };

        Assert.Equal((1113, 198), ResvgMarkdownSvgDocument.ValidateRenderRequest(request));
    }

    [Fact]
    public void RenderAdmissionRevalidatesViewportAfterRecordWithMutation()
    {
        var valid = new MarkdownSvgRenderRequest(1113, 198)
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(1112.5, 197.5),
        };
        MarkdownSvgRenderRequest changed = valid with
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(double.NaN, 197.5),
        };

        MarkdownSvgException failure = Assert.Throws<MarkdownSvgException>(
            () => ResvgMarkdownSvgDocument.ValidateRenderRequest(changed));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1112, 197.5)]
    [InlineData(1112.5, 198.1)]
    public void AdmissionRejectsViewportThatIsNonpositiveOrDoesNotCeilToAllocation(
        double viewportWidth,
        double viewportHeight)
    {
        var request = new MarkdownSvgRenderRequest(1113, 198)
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(viewportWidth, viewportHeight),
        };

        MarkdownSvgException failure = Assert.Throws<MarkdownSvgException>(
            () => ResvgMarkdownSvgDocument.ValidateRenderRequest(request));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public void AdmissionRejectsNonFiniteViewportComponents()
    {
        foreach (MarkdownSvgPhysicalViewport viewport in new[]
        {
            new MarkdownSvgPhysicalViewport(double.NaN, 197.5),
            new MarkdownSvgPhysicalViewport(1112.5, double.PositiveInfinity),
            new MarkdownSvgPhysicalViewport(double.NegativeInfinity, 197.5),
        })
        {
            var request = new MarkdownSvgRenderRequest(1113, 198) { PhysicalViewport = viewport };
            MarkdownSvgException failure = Assert.Throws<MarkdownSvgException>(
                () => ResvgMarkdownSvgDocument.ValidateRenderRequest(request));
            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
        }
    }

    [Fact]
    public void OmittedViewportRetainsIntegerTargetDimensions()
    {
        var request = new MarkdownSvgRenderRequest(1113, 198);

        Assert.Equal((1113, 198), ResvgMarkdownSvgDocument.ValidateRenderRequest(request));
        Assert.True(PhysicalViewportValidation.IsValid(null, 1113, 198));
    }
}
