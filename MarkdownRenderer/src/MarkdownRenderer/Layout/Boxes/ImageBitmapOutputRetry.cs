using System;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Delivers an image box's source-free bitmap-output retry. On the box's
/// dispatcher, once its image work has drained, it repaints an existing tile
/// output or restarts the box's load with the deferred demand; a cancelled
/// retry asks for a repaint so the box can load again. The request state
/// stays with <see cref="ImageBitmapOutputDeferral"/>.
/// </summary>
internal sealed class ImageBitmapOutputRetry
{
    private readonly IImageBitmapOutputHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly CancellationTokenSource _imageLifetime;
    private readonly ImageBitmapOutputDeferral _deferral;

    internal ImageBitmapOutputRetry(
        IImageBitmapOutputHost host,
        MarkdownLayoutContext context,
        CancellationTokenSource imageLifetime,
        ImageBitmapOutputDeferral deferral)
    {
        _host = host;
        _context = context;
        _imageLifetime = imageLifetime;
        _deferral = deferral;
    }

    internal void DispatchBitmapOutputRetry(long epoch)
    {
        try
        {
            if (_context.Dispatcher is { } dispatcher)
            {
                if (!dispatcher.TryEnqueue(() => RunBitmapOutputRetrySafely(epoch)))
                    _deferral.CancelBitmapOutputDeferral(epoch, resetLoad: true);
                return;
            }

            RunBitmapOutputRetrySafely(epoch);
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
            _deferral.CancelBitmapOutputDeferral(epoch, resetLoad: true);
        }
    }

    private void RunBitmapOutputRetrySafely(long epoch)
    {
        try
        {
            RunBitmapOutputRetry(epoch);
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
            _deferral.CancelBitmapOutputDeferral(epoch, resetLoad: true);
        }
    }

    private void RunBitmapOutputRetry(long epoch)
    {
        if (!_deferral.TryReadQueuedRetry(
                epoch,
                out MarkdownSvgRenderDemand? demand,
                out int demandGeneration,
                out int svgGeneration,
                out bool visible,
                out bool tileOutput))
        {
            return;
        }

        bool workDrained = _host.IsImageWorkDrained;
        if (!workDrained || demand is null ||
            !ReferenceEquals(_host.LoadDemand, demand) ||
            demandGeneration != _host.LoadDemandGeneration ||
            svgGeneration != _host.SvgRenderGeneration ||
            _imageLifetime.IsCancellationRequested ||
            (_context.PerformanceSession?.IsDisposed ?? false))
        {
            if (!workDrained)
            {
                _deferral.ReleaseQueuedRetry();
                return;
            }

            _deferral.CancelBitmapOutputDeferral(epoch, resetLoad: true);
            return;
        }

        if (!_deferral.TryBeginRetry(epoch))
            return;

        if (tileOutput)
        {
            // The accepted document/tree remains alive. Repaint queues only
            // the current visible tile work; the source-free waiter never
            // owns or reopens that document.
            _deferral.ClearBitmapOutputDeferral(expectedEpoch: epoch);
            try { _host.RaiseLoadCompleted(layoutInvalidated: false); }
            catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
            return;
        }

        try
        {
            _host.StartLoad(
                isVisible: visible,
                demandOverride: demand,
                loadDemandGeneration: demandGeneration);
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
            _deferral.CancelBitmapOutputDeferral(epoch, resetLoad: true);
            return;
        }

        bool noWork = _host.HasNoImageWork;
        if (noWork)
            _deferral.SettleBitmapOutputRetry(capacityClaimed: false);
    }

    internal void RequestBitmapOutputRepaint()
    {
        MarkdownSvgRenderDemand? demand = _host.LoadDemand;
        int generation = _host.LoadDemandGeneration;
        try
        {
            if (_context.Dispatcher is { } dispatcher)
            {
                _ = dispatcher.TryEnqueue(() => DeliverBitmapOutputRepaint(demand, generation));
            }
            else
            {
                DeliverBitmapOutputRepaint(demand, generation);
            }
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
        }
    }

    private void DeliverBitmapOutputRepaint(MarkdownSvgRenderDemand? demand, int generation)
    {
        if (_host.IsRetired ||
            (_context.PerformanceSession?.IsDisposed ?? false) ||
            !ReferenceEquals(_host.LoadDemand, demand) ||
            _host.LoadDemandGeneration != generation)
        {
            return;
        }

        try { _host.RaiseLoadCompleted(layoutInvalidated: false); }
        catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
    }
}
