using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownAccessibilityGeometryTests
{
    [Fact]
    public void BoundingRectangles_DegenerateRange_ReturnsEmptyWithoutEnumeratingGeometry()
    {
        IEnumerable<AccessibilityRect> ThrowIfEnumerated()
        {
            throw new InvalidOperationException("Degenerate ranges must not request geometry.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        double[] result = AccessibilityGeometry.BuildVisibleBoundingRectangles(
            4,
            4,
            ThrowIfEnumerated(),
            new AccessibilityRect(0, 0, 100, 100));

        Assert.Empty(result);
    }

    [Fact]
    public void BoundingRectangles_ClipsPartiallyVisibleLinesAndDropsOffscreenLines()
    {
        AccessibilityRect[] screenRects =
        [
            new AccessibilityRect(5, 5, 20, 20),
            new AccessibilityRect(90, 90, 30, 30),
            new AccessibilityRect(150, 150, 10, 10),
        ];

        double[] result = AccessibilityGeometry.BuildVisibleBoundingRectangles(
            0,
            20,
            screenRects,
            new AccessibilityRect(10, 10, 100, 100));

        Assert.Equal(
        [
            10d, 10d, 15d, 15d,
            90d, 90d, 20d, 20d,
        ],
            result);
    }

    [Fact]
    public void BoundingRectangles_FullyOffscreenRange_ReturnsEmpty()
    {
        double[] result = AccessibilityGeometry.BuildVisibleBoundingRectangles(
            0,
            3,
            [new AccessibilityRect(200, 200, 30, 20)],
            new AccessibilityRect(0, 0, 100, 100));

        Assert.Empty(result);
    }

    [Fact]
    public void NearestTextRect_UsesOneLinearPassAndReturnsNearestPoint()
    {
        const int count = 25_000;
        int visited = 0;

        IEnumerable<AccessibilityRect> Rectangles()
        {
            for (int index = 0; index < count; index++)
            {
                visited++;
                yield return new AccessibilityRect(index * 20, index * 10, 8, 8);
            }
        }

        bool found = AccessibilityGeometry.TryCoercePointToNearestRect(
            new AccessibilityPoint(20_003, 10_006),
            Rectangles(),
            out AccessibilityPoint result);

        Assert.True(found);
        Assert.Equal(count, visited);
        Assert.Equal(new AccessibilityPoint(20_003, 10_006), result);
    }

    [Fact]
    public void NearestTextRect_NoUsableGeometry_PreservesInputPoint()
    {
        var input = new AccessibilityPoint(24, 32);

        bool found = AccessibilityGeometry.TryCoercePointToNearestRect(
            input,
            [default, new AccessibilityRect(0, 0, 0, 12)],
            out AccessibilityPoint result);

        Assert.False(found);
        Assert.Equal(input, result);
    }
}
