using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Snapshots;

/// <summary>
/// A fixed-height root that counts Measure and Arrange calls and can inject
/// measure, arrange or pass-cancellation failures for lazy-reflow tests.
/// </summary>
internal class LazyReflowProbeBlock(float height) : BlockBox
{
    internal const string MeasureFailure = "probe measure failure";
    internal const string ArrangeFailure = "probe arrange failure";

    private float _measuredHeight = height;

    internal int MeasureCount { get; private set; }
    internal int ArrangeCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal bool ThrowAfterMeasureMutation { get; set; }
    internal bool ThrowAfterNextArrange { get; set; }
    internal bool ThrowOnEveryArrange { get; set; }
    private LayoutPassCancellation? _observedPass;
    internal Action? AfterMeasure { get; set; }

    internal void SetMeasuredHeight(float height) => _measuredHeight = height;

    /// <summary>Throws from Arrange when the snapshot's pass token is canceled.</summary>
    internal void ObservePassCancellation(LayoutPassCancellation pass) => _observedPass = pass;

    internal void ResetCounts()
    {
        MeasureCount = 0;
        ArrangeCount = 0;
    }

    public override float Measure(float availableWidth)
    {
        MeasureCount++;
        Bounds = new Rect(13, 17, availableWidth, _measuredHeight + 19);
        if (ThrowAfterMeasureMutation)
            throw new InvalidOperationException(MeasureFailure);

        Bounds = new Rect(0, 0, availableWidth, _measuredHeight);
        AfterMeasure?.Invoke();
        return _measuredHeight;
    }

    public override void Arrange(float x, float y, float width)
    {
        ArrangeCount++;
        _observedPass?.GetEffectiveToken(CancellationToken.None).ThrowIfCancellationRequested();

        base.Arrange(x, y, width);
        if (ThrowAfterNextArrange)
        {
            ThrowAfterNextArrange = false;
            Bounds = new Rect(x + 9, y + 11, width, Bounds.Height + 13);
            throw new InvalidOperationException(ArrangeFailure);
        }
        if (ThrowOnEveryArrange)
            throw new InvalidOperationException(ArrangeFailure);
    }

    public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

    public override void Dispose()
    {
        DisposeCount++;
    }
}
