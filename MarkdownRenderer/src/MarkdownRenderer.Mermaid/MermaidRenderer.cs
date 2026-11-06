using System.Globalization;
using System.Text;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Owns the native Mermaid engine, its scene cache and its render budgets.
/// Pass one instance to
/// <see cref="MermaidMarkdownExtensions.UseMermaid(MarkdownEngineBuilder, MermaidRenderer)"/>
/// to share them across engines; the caller disposes it.
/// </summary>
/// <remarks>
/// Requests are bounded, and a failed request always falls back to the
/// original fenced code.
/// </remarks>
public sealed class MermaidRenderer : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly MermaidRenderOptions _options;
    private readonly MermaidFontCatalog _fontCatalog;
    private readonly SemaphoreSlim _renderGate;
    private readonly MermaidSceneCache _sceneCache;
    private readonly MermaidRenderLifetime _lifetime;
    private readonly CultureInfo _diagnosticCulture;
    private readonly MermaidRenderDiagnostics _diagnostics;
    private readonly MermaidNativeEngine _nativeEngine;
    private readonly string? _validationError;

    public MermaidRenderer(MermaidRenderOptions? options = null, MermaidFontCatalog? fontCatalog = null)
        : this(options, fontCatalog, renderGate: null)
    {
    }

    /// <summary>Creates a renderer around a caller-supplied render admission gate.</summary>
    /// <param name="options">The render options.</param>
    /// <param name="fontCatalog">The font catalog.</param>
    /// <param name="renderGate">
    /// The gate that admits concurrent native renders, or <see langword="null"/>
    /// to create one sized by <see cref="MermaidRenderBudgets.MaxConcurrentRenders"/>.
    /// The renderer owns the gate and disposes it after its last render.
    /// </param>
    internal MermaidRenderer(
        MermaidRenderOptions? options,
        MermaidFontCatalog? fontCatalog,
        SemaphoreSlim? renderGate)
    {
        MermaidRenderOptions configured = options ?? MermaidRenderOptions.Default;
        _diagnosticCulture = CultureInfo.ReadOnly(
            (CultureInfo)(configured.DiagnosticCulture ?? CultureInfo.CurrentUICulture).Clone());
        _options = configured with
        {
            Budgets = configured.Budgets is null ? null! : configured.Budgets with { },
            DiagnosticCulture = _diagnosticCulture,
        };
        _fontCatalog = fontCatalog ?? MermaidFontCatalog.Default;
        _diagnostics = new MermaidRenderDiagnostics(_options);
        _nativeEngine = new MermaidNativeEngine(_options, _fontCatalog, _diagnostics);
        _options.TryValidate(out _validationError);
        MermaidRenderBudgets budgets = _options.Budgets ?? MermaidRenderBudgets.Default;
        int concurrency = Math.Clamp(budgets.MaxConcurrentRenders, 1, 64);
        _renderGate = renderGate ?? new SemaphoreSlim(concurrency, concurrency);
        long cacheBudget = _options.SceneCacheBudgetBytes is >= 0 and <= MermaidRenderOptions.AbsoluteMaxSceneCacheBudgetBytes
            ? _options.SceneCacheBudgetBytes
            : 0;
        int maxOutstandingRenders = Math.Clamp(
            budgets.MaxOutstandingRenders,
            1,
            MermaidRenderBudgets.AbsoluteMaxOutstandingRenders);
        long maxOutstandingSourceBytes = Math.Clamp(
            budgets.MaxOutstandingSourceBytes,
            1,
            MermaidRenderBudgets.AbsoluteMaxOutstandingSourceBytes);
        _sceneCache = new MermaidSceneCache(
            cacheBudget,
            maxOutstandingRenders,
            maxOutstandingSourceBytes);
        _lifetime = new MermaidRenderLifetime(_renderGate, DisposeResources);
    }

    public MermaidRenderOptions Options => _options;

    public MermaidFontCatalog FontCatalog => _fontCatalog;

    internal CultureInfo DiagnosticCulture => _diagnosticCulture;

    public MermaidNativeRuntimeInfo RuntimeInfo => MermaidNativeRuntime.Probe();

    internal ValueTask<MermaidRenderResult> RenderAsync(
        string source,
        CancellationToken cancellationToken = default)
        => RenderCoreAsync(source, cancellationToken, scheduleNativeRender: true);

    /// <summary>
    /// Renders when the caller already owns a background worker. Native work is
    /// performed on that worker rather than dispatching a nested thread-pool job.
    /// </summary>
    internal ValueTask<MermaidRenderResult> RenderOnWorkerAsync(
        string source,
        CancellationToken cancellationToken = default)
        => RenderCoreAsync(source, cancellationToken, scheduleNativeRender: false);

    private async ValueTask<MermaidRenderResult> RenderCoreAsync(
        string source,
        CancellationToken cancellationToken,
        bool scheduleNativeRender)
    {
        // The lease covers validation, queueing, and native rendering. Dispose is
        // non-blocking, rejects subsequent requests, and defers semaphore disposal
        // until every request that already entered this method has released it.
        using MermaidRenderLifetime.Lease operation = _lifetime.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        CultureInfo diagnosticCulture = _diagnosticCulture;
        if (TryRejectRequest(source, diagnosticCulture, out long deadlineTimestamp, out int sourceByteLength) is { } rejection)
        {
            return rejection;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadlineCancellation =
            MermaidDeadline.CreateCancellation(deadlineTimestamp);
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token);
        MermaidSceneCacheKey cacheKey = MermaidSceneCacheKey.Create(
            source,
            sourceByteLength,
            _options,
            _fontCatalog,
            diagnosticCulture);
        MermaidRenderLifetime.Lease producerLease = operation.Fork();
        bool producerLeaseTransferred = false;
        try
        {
            MermaidRenderResult result = await _sceneCache.GetOrRenderAsync(
                cacheKey,
                source,
                producerCancellation =>
                {
                    producerLeaseTransferred = true;
                    return RenderQueuedAsync(
                        source,
                        sourceByteLength,
                        producerLease,
                        producerCancellation,
                        deadlineTimestamp,
                        diagnosticCulture,
                        scheduleNativeRender);
                },
                () => _diagnostics.OverloadedResult(source, diagnosticCulture),
                () => _diagnostics.TimedOutResult(source, diagnosticCulture),
                deadlineTimestamp,
                requestCancellation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return deadlineCancellation.IsCancellationRequested &&
                result.Status == MermaidRenderStatus.Success
                    ? _diagnostics.TimedOutResult(source, diagnosticCulture)
                    : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            return _diagnostics.TimedOutResult(source, diagnosticCulture);
        }
        finally
        {
            if (!producerLeaseTransferred)
                producerLease.Dispose();
        }
    }

    /// <summary>
    /// Rejects null, misconfigured and oversized requests before any cache or
    /// native work. The request deadline starts here, before UTF-8 measurement,
    /// and is returned with the measured byte length when the request is accepted.
    /// </summary>
    private MermaidRenderResult? TryRejectRequest(
        string source,
        CultureInfo diagnosticCulture,
        out long deadlineTimestamp,
        out int sourceByteLength)
    {
        deadlineTimestamp = 0;
        sourceByteLength = 0;
        if (source is null)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.InvalidInput,
                string.Empty,
                "MMR0003",
                MermaidStringKeys.SourceNull,
                diagnosticCulture,
                "The Mermaid source cannot be null.");
        }

        if (_validationError is not null)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.InvalidOptions,
                source,
                "MMR0001",
                MermaidStringKeys.InvalidOptions,
                diagnosticCulture,
                "The Mermaid rendering options are invalid.");
        }

        if (_options.Layout == MermaidLayoutMode.Elk)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.UnsupportedLayout,
                source,
                "MMR0002",
                MermaidStringKeys.ElkLayoutUnavailable,
                diagnosticCulture,
                "ELK layout is not included in MarkdownRenderer.Mermaid.");
        }

        if (source.Length > _options.Budgets.MaxSourceBytes)
        {
            return _diagnostics.SourceTooLargeResult(source, diagnosticCulture);
        }

        // Start the request deadline before UTF-8 conversion and cache-key
        // hashing so large, valid inputs cannot consume unbounded synchronous
        // preprocessing time outside the advertised end-to-end budget.
        deadlineTimestamp = MermaidDeadline.Start(_options.Budgets.Deadline);
        try
        {
            sourceByteLength = StrictUtf8.GetByteCount(source);
        }
        catch (EncoderFallbackException)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.InvalidInput,
                source,
                "MMR0003",
                MermaidStringKeys.SourceInvalidUtf16,
                diagnosticCulture,
                "The Mermaid source contains an unpaired UTF-16 surrogate.");
        }

        if (sourceByteLength > _options.Budgets.MaxSourceBytes)
        {
            return _diagnostics.SourceTooLargeResult(source, diagnosticCulture);
        }

        return null;
    }

    private async Task<MermaidRenderResult> RenderQueuedAsync(
        string source,
        int sourceByteLength,
        MermaidRenderLifetime.Lease producerLease,
        CancellationToken cancellationToken,
        long deadlineTimestamp,
        CultureInfo diagnosticCulture,
        bool scheduleNativeRender)
    {
        using (producerLease)
        using (CancellationTokenSource deadlineCancellation =
            MermaidDeadline.CreateCancellation(deadlineTimestamp))
        using (CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token))
        {
            bool enteredRenderGate = false;
            try
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                byte[] sourceBytes = StrictUtf8.GetBytes(source);
                if (sourceBytes.Length != sourceByteLength)
                    throw new InvalidOperationException("The validated Mermaid UTF-8 length changed unexpectedly.");
                operationCancellation.Token.ThrowIfCancellationRequested();

                await _renderGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
                enteredRenderGate = true;
                operationCancellation.Token.ThrowIfCancellationRequested();

                MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();
                operationCancellation.Token.ThrowIfCancellationRequested();
                if (!runtime.IsAvailable)
                {
                    return _diagnostics.Result(
                        MermaidRenderStatus.EngineUnavailable,
                        source,
                        "MMR0006",
                        MermaidStringKeys.EngineUnavailable,
                        diagnosticCulture,
                        "The native Mermaid engine is unavailable.");
                }

                uint nativeDeadlineMilliseconds = GetNativeDeadlineMilliseconds(deadlineTimestamp);
                if (nativeDeadlineMilliseconds == 0)
                    return _diagnostics.TimedOutResult(source, diagnosticCulture);

                MermaidRenderResult result = await RenderNativeAsync(
                    scheduleNativeRender,
                    source,
                    sourceBytes,
                    operationCancellation.Token,
                    cancellationToken,
                    deadlineCancellation.Token,
                    diagnosticCulture,
                    nativeDeadlineMilliseconds).ConfigureAwait(false);

                operationCancellation.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                return _diagnostics.CooperativeCancellationResult(
                    source,
                    cancellationToken,
                    deadlineCancellation.Token,
                    diagnosticCulture);
            }
            finally
            {
                if (enteredRenderGate)
                    _renderGate.Release();
            }
        }
    }

    /// <summary>
    /// Runs the native render on the thread pool for the asynchronous entry
    /// point, or inline on the caller's thread for the synchronous one.
    /// </summary>
    private Task<MermaidRenderResult> RenderNativeAsync(
        bool scheduleNativeRender,
        string source,
        byte[] sourceBytes,
        CancellationToken operationCancellation,
        CancellationToken producerCancellation,
        CancellationToken deadlineCancellation,
        CultureInfo diagnosticCulture,
        uint nativeDeadlineMilliseconds)
    {
        if (scheduleNativeRender)
        {
            return Task.Run(
                () => _nativeEngine.RenderNative(
                    source,
                    sourceBytes,
                    operationCancellation,
                    producerCancellation,
                    deadlineCancellation,
                    diagnosticCulture,
                    nativeDeadlineMilliseconds),
                CancellationToken.None);
        }

        return Task.FromResult(_nativeEngine.RenderNative(
            source,
            sourceBytes,
            operationCancellation,
            producerCancellation,
            deadlineCancellation,
            diagnosticCulture,
            nativeDeadlineMilliseconds));
    }

    public void Dispose()
    {
        _lifetime.Dispose();
    }

    private void DisposeResources()
    {
        _sceneCache.Dispose();
        _nativeEngine.Dispose();
    }

    internal string ResolveString(
        string resourceKey,
        CultureInfo diagnosticCulture,
        string englishFormat,
        params object?[] arguments) =>
        _diagnostics.ResolveString(resourceKey, diagnosticCulture, englishFormat, arguments);
    private static uint GetNativeDeadlineMilliseconds(long deadlineTimestamp)
    {
        TimeSpan remaining = MermaidDeadline.GetRemaining(deadlineTimestamp);
        if (remaining <= TimeSpan.Zero)
            return 0;

        double milliseconds = Math.Ceiling(remaining.TotalMilliseconds);
        return milliseconds >= uint.MaxValue ? uint.MaxValue : Math.Max(1u, (uint)milliseconds);
    }
}
