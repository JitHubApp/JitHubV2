using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// One rebuild of the document: capture inputs, parse, lay out, and commit
/// the snapshot while its generation is current, then report completion or
/// failure. Owns the parse cache and the committed pipeline's timings.
/// </summary>
internal sealed class RebuildPipeline
{
    private readonly MarkdownRendererControl _control;
    private readonly IRendererEventSink _sink;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly RebuildScheduler _rebuild;
    private readonly RebuildInputCapture _inputs;
    private readonly RebuildLayoutSetup _layoutSetup;
    private readonly LayoutWidthWatch _width;
    private readonly ImageSourcePrefetch _imagePrefetch;
    private readonly RendererAutomation _automation;
    private readonly ReplacementPublisher _publisher;
    private readonly ReplacementPresentation _presentation;
    private readonly ParsedMarkdownCache _parseCache = new();
    private readonly PipelineTimings _timings = new();

    internal RebuildPipeline(
        MarkdownRendererControl control,
        IRendererEventSink sink,
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        RebuildScheduler rebuild,
        RebuildInputCapture inputs,
        RebuildLayoutSetup layoutSetup,
        LayoutWidthWatch width,
        ImageSourcePrefetch imagePrefetch,
        RendererAutomation automation,
        ReplacementPublisher publisher,
        ReplacementPresentation presentation)
    {
        _control = control;
        _sink = sink;
        _lifetime = lifetime;
        _surface = surface;
        _viewport = viewport;
        _rebuild = rebuild;
        _inputs = inputs;
        _layoutSetup = layoutSetup;
        _width = width;
        _imagePrefetch = imagePrefetch;
        _automation = automation;
        _publisher = publisher;
        _presentation = presentation;
    }

    /// <summary>The stage timings of the committed snapshot's pipeline.</summary>
    internal MarkdownPipelineTimingSnapshot LastTimings => _timings.Last;

    /// <summary>Forgets this control's cached theme resource-key discovery.</summary>
    internal void InvalidateThemeResourceKeys() => _layoutSetup.InvalidateThemeResourceKeys();

    /// <summary>Parses with the legacy parser through this control's parse cache.</summary>
    internal Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken) =>
        _parseCache.GetAsync(source, registry, cancellationToken);

    internal async Task RunAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        try
        {
            await BuildAsync(
                ct,
                reason,
                generation,
                pipelineStartTimestamp,
                sourceUtf16Bytes).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && generation == _rebuild.Generation)
            {
                RaiseRenderCompleted(generation);
            }
        }
        catch (OperationCanceledException) { /* expected – a new build was requested */ }
        catch (MarkdownLayoutRetirementException retirementFailure)
        {
            if (retirementFailure.TakePrimaryFailure() is { } primaryFailure &&
                primaryFailure is not OperationCanceledException)
            {
                RaiseRenderFailed(generation, primaryFailure);
            }

            // Resource retirement is part of pipeline completion, not an
            // ordinary render diagnostic. Keep this task faulted so a control
            // shutdown barrier cannot report success after failed cleanup.
            throw;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild failed: {ex}");
            RaiseRenderFailed(generation, ex);
        }
    }

    private void RaiseRenderCompleted(long generation)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || generation != _rebuild.Generation)
            return;

        try
        {
            _sink.InvokeRenderCompleted();
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderCompleted subscriber failed: {ex.Message}");
        }

        _imagePrefetch.StartPending();
    }

    private void RaiseRenderFailed(long generation, Exception exception)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || generation != _rebuild.Generation)
            return;

        try
        {
            _sink.InvokeRenderFailed(new MarkdownRenderFailedEventArgs(exception));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderFailed subscriber failed: {ex.Message}");
        }
    }

    private async Task BuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        if (_surface.Canvas is null || _surface.Overlay is null || _surface.Root is null) return;
        var width = (float)Math.Max(50, _control.ActualWidth);
        _width.Record(width);

        RebuildSourceInputs source = _inputs.CaptureSource();
        if (ct.IsCancellationRequested || generation != _rebuild.Generation)
            return;

        RebuildParseResult? parse = await RebuildParse.ParseAsync(source, _parseCache, _control, ct).ConfigureAwait(true);
        if (parse is null || ct.IsCancellationRequested || generation != _rebuild.Generation)
            return;

        RebuildLayoutPlan plan = _layoutSetup.CreatePlan(
            source,
            _inputs.CaptureLayout(),
            parse,
            reason,
            width,
            ct);
        Exception? primaryFailure = null;
        try
        {
            await BuildAndCommitAsync(plan, generation, pipelineStartTimestamp, sourceUtf16Bytes, ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            // The control-level drain is epoch-based: work registered after its
            // disposal snapshot belongs to a later epoch. Drain this build's
            // own registrations before its pipeline task completes so provider
            // retirement cannot race failed-build image teardown.
            await plan.Context.DrainOwnedRetirementsAsync(primaryFailure).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Builds the snapshot <paramref name="plan"/> describes and, while
    /// <paramref name="generation"/> is still current, replaces the published
    /// snapshot with it. An unpublished snapshot is always retired.
    /// </summary>
    private async Task BuildAndCommitAsync(
        RebuildLayoutPlan plan,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes,
        CancellationToken ct)
    {
        MarkdownLayoutContext ctx = plan.Context;
        LayoutBuilder builder = _layoutSetup.CreateBuilder(plan);
        if (ct.IsCancellationRequested || generation != _rebuild.Generation)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        bool captureLayoutCpu = plan.PerformanceSession is not null;
        Action<long>? workerCompleted = null;
        using var initialLayoutDispatcherProbe =
            _timings.StartInitialDispatcherProbe(_control.DispatcherQueue, plan.LayoutDiagnostics);
        captureLayoutCpu |= plan.LayoutDiagnostics is not null;
        if (initialLayoutDispatcherProbe is not null)
            workerCompleted = initialLayoutDispatcherProbe.EnqueueWorkerCompletionSentinel;
        RebuildLayoutRunResult? run = await RebuildLayoutRun.RunAsync(
            builder, ctx, plan.Parse.Parsed, plan.Width, viewportTop, viewportHeight,
            hasCustomBlockEmbedMeasurement: plan.Inputs.EmbedFactory is not null,
            plan.PerformanceSession, captureLayoutCpu, workerCompleted, ct).ConfigureAwait(true);
        if (run is null)
            return;
        MarkdownLayoutDiagnosticsSnapshot layoutDiagnosticSnapshot =
            plan.LayoutDiagnostics?.Capture() ?? default;
        LayoutSnapshot? snapshot = run.Snapshot;
        if (snapshot is null || ct.IsCancellationRequested || generation != _rebuild.Generation)
        {
            if (snapshot is not null)
                ctx.RegisterRetirement(snapshot.Retire());
            return;
        }

        // From this point the snapshot holds GPU-side CanvasTextLayout objects.
        // If we are cancelled before committing, dispose it to avoid a native-memory leak.
        // `committed` tracks whether the snapshot was published; only an
        // unpublished snapshot is retired here (once published, _published owns it).
        bool committed = false;
        var timeline = new ReplacementTimeline(Stopwatch.GetTimestamp());
        try
        {
            if (!_publisher.TryPublish(
                    plan, snapshot, generation, pipelineStartTimestamp, sourceUtf16Bytes, ct,
                    out ReplacementPublication? publication))
            {
                return;
            }

            committed = true;
            _automation.NotifySnapshotChanged();
            _timings.CompleteInitialDispatcherProbe(initialLayoutDispatcherProbe, plan.LayoutDiagnostics);
            _presentation.Finish(plan, snapshot, generation, publication, timeline);
            _timings.PublishCommitted(
                generation, sourceUtf16Bytes, plan, run,
                layoutDiagnosticSnapshot,
                timeline);
        }
        finally
        {
            // Every cancellation/generation retry after construction must release
            // the unpublished Win2D layout. Once committed, _published owns it.
            if (!committed)
                ctx.RegisterRetirement(snapshot.Retire());
        }
    }
}
