using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Boundary checks for <see cref="GestureHitTestBands.TryCreateGestureHitTestBand"/>: malformed
/// contacts, the 256-DIP contact admission limit, non-finite gestures, and clamping.
/// </summary>
internal static class GestureHitTestBandAssertions
{
    public static bool TryCreateGestureHitTestBand(
        Point gestureCanvas,
        double rasterizationScale,
        bool includeContact,
        TouchContactGeometry capturedContact,
        out LazyLayoutBand band) => GestureHitTestBands.TryCreateGestureHitTestBand(
            gestureCanvas,
            rasterizationScale,
            includeContact,
            capturedContact,
            out band);

    public static void AssertMalformedContactsFallBackToTheGestureBand(
        Point gesture,
        double rasterizationScale,
        double slop)
    {
        // Bad contacts are ignored rather than allowed to turn one gesture
        // into an unbounded realization request. The original finite gesture
        // remains a valid readiness band.
        TouchContactGeometry[] malformedContacts =
        [
            new TouchContactGeometry(20, 90, 0, 20),
            new TouchContactGeometry(20, 90, -1, 20),
            new TouchContactGeometry(20, 90, double.NaN, 20),
            new TouchContactGeometry(20, 90, 257, 20),
            new TouchContactGeometry(20, 90, 10, 257),
            new TouchContactGeometry(20, 400, 10, 10),
        ];
        foreach (TouchContactGeometry contact in malformedContacts)
        {
            Assert.True(TryCreateGestureHitTestBand(
                gesture,
                rasterizationScale,
                includeContact: true,
                capturedContact: contact,
                out LazyLayoutBand gestureOnly));
            Assert.Equal(gesture.Y - slop, gestureOnly.Top);
            Assert.Equal(gesture.Y + slop, gestureOnly.Bottom);
        }
    }

    public static void AssertContactAdmissionBoundaryIsInclusiveAt256Dips()
    {
        // The contact admission boundary is inclusive at 256 DIPs in both
        // dimensions and distance, but one fractional DIP beyond it is
        // ignored. This avoids an accidental document-scale realization band.
        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(-128, -28, 256, 256),
            out LazyLayoutBand maximumContact));
        Assert.Equal(0, maximumContact.Top);
        Assert.Equal(232, maximumContact.Bottom);

        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(250, 80, 12, 40),
            out LazyLayoutBand boundaryContact));
        Assert.Equal(76, boundaryContact.Top);
        Assert.Equal(124, boundaryContact.Bottom);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(250.5, 80, 12, 40),
            out LazyLayoutBand beyondBoundary));
        Assert.Equal(96, beyondBoundary.Top);
        Assert.Equal(104, beyondBoundary.Bottom);
    }

    public static void AssertNonFiniteGesturePointsAreRejected(
        double rasterizationScale,
        TouchContactGeometry captured)
    {
        Assert.False(TryCreateGestureHitTestBand(
            new Point(double.NaN, 100),
            rasterizationScale,
            includeContact: true,
            capturedContact: captured,
            out _));
        Assert.False(TryCreateGestureHitTestBand(
            new Point(24, double.PositiveInfinity),
            rasterizationScale,
            includeContact: false,
            capturedContact: default,
            out _));
    }

    public static void AssertBandsClampToTheDocumentAndFallBackFromInvalidScales()
    {
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 2),
            1,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand clampedTop));
        Assert.Equal(0, clampedTop.Top);
        Assert.Equal(6, clampedTop.Bottom);

        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: 0,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand invalidScaleFallback));
        Assert.Equal(96, invalidScaleFallback.Top);
        Assert.Equal(104, invalidScaleFallback.Bottom);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: double.NaN,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand nonFiniteScaleFallback));
        Assert.Equal(invalidScaleFallback, nonFiniteScaleFallback);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, -20),
            rasterizationScale: 1,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand entirelyAboveDocument));
        Assert.Equal(new LazyLayoutBand(0, 0), entirelyAboveDocument);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: 0.001,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand cappedScaleSlop));
        Assert.Equal(0, cappedScaleSlop.Top);
        Assert.Equal(356, cappedScaleSlop.Bottom);
    }
}
