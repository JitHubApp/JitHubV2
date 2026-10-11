using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SourceReplayTerminalClampContractTests
{
    private const int ViewportHeight = 100;
    private const int EdgeDocumentHeight = 1650;
    private const int NativeEstimatedHeight = 1810;
    private const double EdgeMaximum = 15.5;

    [Fact]
    public void CompleteTerminalSuffixPreservesTheNativeProfileAndEdgeBottom()
    {
        var native = NativeProfile();
        double[] actual = native.Select(viewport => Math.Min(viewport.CaptureOffset, EdgeMaximum)).ToArray();
        var observations = TerminalObservations();

        Assert.True(SourceReplayTerminalClampContract.IsComplete(native, actual, observations,
            EdgeDocumentHeight, NativeEstimatedHeight, ViewportHeight,
            NativeReadmeIdentity(), EdgeReadmeIdentity()));

        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, actual,
            observations[..^1], EdgeDocumentHeight, NativeEstimatedHeight, ViewportHeight));
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, actual,
            [.. observations, observations[^1]], EdgeDocumentHeight, NativeEstimatedHeight,
            ViewportHeight));
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, actual, observations,
            EdgeDocumentHeight, 1900, ViewportHeight),
            "the unchanged 110% native/Edge extent envelope also applies to clamped replay");
    }

    [Fact]
    public void TerminalObservationsMustBindEveryMovementAndCaptureToThePhysicalBottom()
    {
        var native = NativeProfile();
        double[] actual = native.Select(viewport => Math.Min(viewport.CaptureOffset, EdgeMaximum)).ToArray();
        SourceReplayTerminalClampObservation[] baseline = TerminalObservations();

        foreach (SourceReplayTerminalClampObservation[] invalid in new[]
        {
            Replace(baseline, 0, baseline[0] with { MovementIndex = -1 }),
            Replace(baseline, 0, baseline[0] with { RequestedOffsetViewportUnits = 16.0 }),
            Replace(baseline, 0, baseline[0] with { ActualOffsetViewportUnits = 15.0 }),
            Replace(baseline, 0, baseline[0] with { MaximumOffsetViewportUnits = 16.2 }),
            Replace(baseline, 0, baseline[0] with { DocumentHeightPx = 1649 }),
            Replace(baseline, 0, baseline[0] with { ViewportHeightPx = 101 }),
        })
        {
            Assert.False(SourceReplayTerminalClampContract.IsComplete(native, actual, invalid,
                EdgeDocumentHeight, NativeEstimatedHeight, ViewportHeight));
        }

        actual[^1] = 15.0;
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, actual, baseline,
            EdgeDocumentHeight, NativeEstimatedHeight, ViewportHeight));
    }

    [Fact]
    public void RankEightOfTenClampIsAcceptedOnlyForTheExactSourceAndMatchingPhysicalExtent()
    {
        const int viewportHeight = 611;
        const int renderedHeight = 4850;
        const int nativeEstimatedHeight = 5214;
        double edgeMaximum = (renderedHeight - viewportHeight) / (double)viewportHeight;
        double nativeMaximum = (nativeEstimatedHeight - viewportHeight) / (double)viewportHeight;
        (double CaptureOffset, IReadOnlyList<double> MovementOffsets)[] native =
            Enumerable.Range(0, 10)
                .Select(index =>
                {
                    double offset = index == 9 ? nativeMaximum : index * 0.9;
                    return (offset, (IReadOnlyList<double>)(index == 0 ? [] : [offset]));
                })
                .ToArray();
        double[] captured = native.Select(viewport => Math.Min(viewport.CaptureOffset, edgeMaximum)).ToArray();
        SourceReplayTerminalClampObservation[] observations = Enumerable.Range(8, 2)
            .SelectMany(index => new[]
            {
                PhysicalBottomObservation(index, 0, native[index].CaptureOffset,
                    edgeMaximum, renderedHeight, viewportHeight),
                PhysicalBottomObservation(index, -1, native[index].CaptureOffset,
                    edgeMaximum, renderedHeight, viewportHeight),
            })
            .ToArray();

        Assert.InRange(edgeMaximum, 6.937, 6.939);
        Assert.Equal(7.2, native[8].CaptureOffset, 10);
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, captured, observations,
            renderedHeight, nativeEstimatedHeight, viewportHeight),
            "a clamped traversal cannot pass without source identity binding");
        Assert.True(SourceReplayTerminalClampContract.IsComplete(native, captured, observations,
            renderedHeight, nativeEstimatedHeight, viewportHeight,
            NativeReadmeIdentity(), EdgeReadmeIdentity()));
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, captured, observations,
            renderedHeight, nativeEstimatedHeight, viewportHeight,
            NativeReadmeIdentity(), EdgeReadmeIdentity() with
            {
                DecodedTextSha256 = new string('d', 64),
            }), "a terminal clamp cannot borrow geometry from a different decoded README source");
    }

    [Fact]
    public void MidPageClampWithMoreThanTenPercentExtentMismatchIsRejected()
    {
        const int viewportHeight = 611;
        const int renderedHeight = 3000;
        const int nativeEstimatedHeight = 5214;
        double edgeMaximum = (renderedHeight - viewportHeight) / (double)viewportHeight;
        double nativeMaximum = (nativeEstimatedHeight - viewportHeight) / (double)viewportHeight;
        (double CaptureOffset, IReadOnlyList<double> MovementOffsets)[] native =
            Enumerable.Range(0, 10)
                .Select(index =>
                {
                    double offset = index == 9 ? nativeMaximum : index * 0.9;
                    return (offset, (IReadOnlyList<double>)(index == 0 ? [] : [offset]));
                })
                .ToArray();
        double[] captured = native.Select(viewport => Math.Min(viewport.CaptureOffset, edgeMaximum)).ToArray();
        SourceReplayTerminalClampObservation[] observations = Enumerable.Range(5, 5)
            .SelectMany(index => new[]
            {
                PhysicalBottomObservation(index, 0, native[index].CaptureOffset,
                    edgeMaximum, renderedHeight, viewportHeight),
                PhysicalBottomObservation(index, -1, native[index].CaptureOffset,
                    edgeMaximum, renderedHeight, viewportHeight),
            })
            .ToArray();

        Assert.True(nativeEstimatedHeight / (double)renderedHeight > 1.10);
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, captured, observations,
            renderedHeight, nativeEstimatedHeight, viewportHeight,
            NativeReadmeIdentity(), EdgeReadmeIdentity()));
    }

    [Fact]
    public void EarlyClampAndReentryAreNotTerminalSuffixes()
    {
        var native = NativeProfile();
        double[] captured = native.Select(viewport => viewport.CaptureOffset).ToArray();
        Assert.True(SourceReplayTerminalClampContract.IsComplete(native, captured, [],
            1900, NativeEstimatedHeight, ViewportHeight));

        captured[3] += 0.5;
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, captured, [],
            1900, NativeEstimatedHeight, ViewportHeight));

        captured = native.Select(viewport => viewport.CaptureOffset).ToArray();
        captured[3] = captured[2];
        Assert.False(SourceReplayTerminalClampContract.IsComplete(native, captured, [],
            1900, NativeEstimatedHeight, ViewportHeight),
            "duplicate capture offsets are allowed only in an evidenced terminal clamp");

        var earlyNative = NativeProfile();
        earlyNative[17] = (16.0, [16.0]);
        var earlyActual = earlyNative.Select(viewport => Math.Min(viewport.CaptureOffset, EdgeMaximum)).ToArray();
        SourceReplayTerminalClampObservation[] earlyObservations =
        [
            Observation(17, 0, 16.0), Observation(17, -1, 16.0),
            .. TerminalObservations(),
        ];
        Assert.False(SourceReplayTerminalClampContract.IsComplete(earlyNative, earlyActual,
            earlyObservations, EdgeDocumentHeight, NativeEstimatedHeight, ViewportHeight));

        var reentryNative = NativeProfile();
        reentryNative[19] = (15.4, [15.4]);
        var reentryActual = reentryNative.Select(viewport => Math.Min(viewport.CaptureOffset, EdgeMaximum)).ToArray();
        Assert.False(SourceReplayTerminalClampContract.IsComplete(reentryNative, reentryActual,
            TerminalObservations()[..2], EdgeDocumentHeight, NativeEstimatedHeight,
            ViewportHeight));
    }

    [Fact]
    public void ReportedStepRatioMustMatchMeasuredCapturesIncludingTerminalClamp()
    {
        var native = NativeProfile();
        double[] captured = native.Select(viewport =>
            Math.Min(viewport.CaptureOffset, EdgeMaximum)).ToArray();
        double measuredRatio = (captured[^1] - captured[0]) / (captured.Length - 1);

        Assert.True(SourceReplayTerminalClampContract.IsComplete(native, captured,
            TerminalObservations(), EdgeDocumentHeight, NativeEstimatedHeight,
            ViewportHeight, NativeReadmeIdentity(), EdgeReadmeIdentity()));
        Assert.True(SourceReplayTerminalClampContract.HasConsistentMeasuredStepRatio(
            captured, measuredRatio, ViewportHeight));
        Assert.False(SourceReplayTerminalClampContract.HasConsistentMeasuredStepRatio(
            captured, 0.9, ViewportHeight),
            "an unclamped native mean cannot be substituted for the measured Edge mean");
        Assert.False(SourceReplayTerminalClampContract.HasConsistentMeasuredStepRatio(
            captured, double.NaN, ViewportHeight));
        Assert.False(SourceReplayTerminalClampContract.HasConsistentMeasuredStepRatio(
            [0], measuredRatio, ViewportHeight));
    }

    private static (double CaptureOffset, IReadOnlyList<double> MovementOffsets)[] NativeProfile() =>
        Enumerable.Range(0, 20)
            .Select(index =>
            {
                double offset = index * 0.9;
                return (offset, (IReadOnlyList<double>)(index == 0 ? [] : [offset]));
            })
            .ToArray();

    private static SourceReplayTerminalClampObservation[] TerminalObservations() =>
    [
        Observation(18, 0, 16.2),
        Observation(18, -1, 16.2),
        Observation(19, 0, 17.1),
        Observation(19, -1, 17.1),
    ];

    private static SourceReplayTerminalClampObservation Observation(
        int viewportIndex, int movementIndex, double requested) => new()
    {
        ViewportIndex = viewportIndex,
        MovementIndex = movementIndex,
        RequestedOffsetViewportUnits = requested,
        ActualOffsetViewportUnits = EdgeMaximum,
        MaximumOffsetViewportUnits = EdgeMaximum,
        DocumentHeightPx = EdgeDocumentHeight,
        ViewportHeightPx = ViewportHeight,
    };

    private static SourceReplayTerminalClampObservation PhysicalBottomObservation(
        int viewportIndex,
        int movementIndex,
        double requested,
        double maximumOffset,
        int documentHeight,
        int viewportHeight) => new()
    {
        ViewportIndex = viewportIndex,
        MovementIndex = movementIndex,
        RequestedOffsetViewportUnits = requested,
        ActualOffsetViewportUnits = maximumOffset,
        MaximumOffsetViewportUnits = maximumOffset,
        DocumentHeightPx = documentHeight,
        ViewportHeightPx = viewportHeight,
    };

    private static SourceReplayReadmeIdentity NativeReadmeIdentity() => new(
        new string('a', 40), new string('b', 64), 2550, new string('c', 64));

    private static SourceReplayReadmeIdentity EdgeReadmeIdentity() => new(
        new string('a', 40), new string('b', 64), 2550, new string('c', 64));

    private static SourceReplayTerminalClampObservation[] Replace(
        SourceReplayTerminalClampObservation[] baseline, int index,
        SourceReplayTerminalClampObservation replacement)
    {
        SourceReplayTerminalClampObservation[] copy = [.. baseline];
        copy[index] = replacement;
        return copy;
    }
}
