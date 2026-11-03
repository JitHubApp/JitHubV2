using System.Globalization;
using System.Runtime.InteropServices;
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
    private const int NativeCopyChunkBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly MermaidRenderOptions _options;
    private readonly MermaidFontCatalog _fontCatalog;
    private readonly SemaphoreSlim _renderGate;
    private readonly MermaidSceneCache _sceneCache;
    private readonly MermaidRenderLifetime _lifetime;
    private readonly CultureInfo _diagnosticCulture;
    private readonly MermaidRenderDiagnostics _diagnostics;
    private readonly object _engineSync = new();
    private readonly string? _validationError;
    private MermaidEngineHandle? _engine;

    public MermaidRenderer(MermaidRenderOptions? options = null, MermaidFontCatalog? fontCatalog = null)
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
        _options.TryValidate(out _validationError);
        MermaidRenderBudgets budgets = _options.Budgets ?? MermaidRenderBudgets.Default;
        int concurrency = Math.Clamp(budgets.MaxConcurrentRenders, 1, 64);
        _renderGate = new SemaphoreSlim(concurrency, concurrency);
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

                MermaidRenderResult result;
                if (scheduleNativeRender)
                {
                    result = await Task.Run(
                        () => RenderNative(
                            source,
                            sourceBytes,
                            operationCancellation.Token,
                            cancellationToken,
                            deadlineCancellation.Token,
                            diagnosticCulture,
                            nativeDeadlineMilliseconds),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    result = RenderNative(
                        source,
                        sourceBytes,
                        operationCancellation.Token,
                        cancellationToken,
                        deadlineCancellation.Token,
                        diagnosticCulture,
                        nativeDeadlineMilliseconds);
                }

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

    public void Dispose()
    {
        _lifetime.Dispose();
    }

    private unsafe MermaidRenderResult RenderNative(
        string source,
        byte[] sourceBytes,
        CancellationToken operationCancellation,
        CancellationToken producerCancellation,
        CancellationToken deadlineCancellation,
        CultureInfo diagnosticCulture,
        uint nativeDeadlineMilliseconds)
    {
        try
        {
            operationCancellation.ThrowIfCancellationRequested();
            NativeStatus status = GetOrCreateEngine(out MermaidEngineHandle? engine);
            operationCancellation.ThrowIfCancellationRequested();
            if (status != NativeStatus.Success || engine is null)
            {
                return _diagnostics.NativeFailure(status, source, diagnosticCulture);
            }

            status = NativeMethods.CancellationCreate(out nint rawCancellation);
            if (status != NativeStatus.Success || rawCancellation == 0)
            {
                return _diagnostics.NativeFailure(status, source, diagnosticCulture);
            }

            using var nativeCancellation = new MermaidCancellationHandle(rawCancellation);
            using CancellationTokenRegistration registration = operationCancellation.Register(
                static state => ((MermaidCancellationHandle)state!).Request(),
                nativeCancellation);

            var renderOptions = CreateNativeRenderOptions(nativeDeadlineMilliseconds);
            try
            {
                fixed (byte* sourcePointer = sourceBytes)
                {
                    status = NativeMethods.Render(
                        engine,
                        sourcePointer,
                        checked((uint)sourceBytes.Length),
                        in renderOptions,
                        nativeCancellation,
                        out nint rawBuffer);

                    if (status != NativeStatus.Success || rawBuffer == 0)
                    {
                        return NativeRenderFailure(
                            status,
                            source,
                            producerCancellation,
                            deadlineCancellation,
                            diagnosticCulture);
                    }

                    return MaterializeNativeScene(rawBuffer, source, operationCancellation, diagnosticCulture);
                }
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                return _diagnostics.CooperativeCancellationResult(
                    source,
                    producerCancellation,
                    deadlineCancellation,
                    diagnosticCulture);
            }
        }
        catch (BadImageFormatException)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.EngineUnavailable,
                source,
                "MMR0006",
                MermaidStringKeys.EngineWrongArchitecture,
                diagnosticCulture,
                "The native Mermaid engine has the wrong architecture.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or FileLoadException)
        {
            return _diagnostics.Result(
                MermaidRenderStatus.EngineUnavailable,
                source,
                "MMR0006",
                MermaidStringKeys.EngineBecameUnavailable,
                diagnosticCulture,
                "The optional native Mermaid engine became unavailable.");
        }
    }

    /// <summary>
    /// Maps a native render call that produced no buffer to its result: native
    /// timeout and cancellation are reported like their managed counterparts.
    /// </summary>
    private MermaidRenderResult NativeRenderFailure(
        NativeStatus status,
        string source,
        CancellationToken producerCancellation,
        CancellationToken deadlineCancellation,
        CultureInfo diagnosticCulture)
    {
        if (status is NativeStatus.Cancelled or NativeStatus.TimedOut)
        {
            return status == NativeStatus.TimedOut
                ? _diagnostics.TimedOutResult(source, diagnosticCulture)
                : _diagnostics.CooperativeCancellationResult(
                    source,
                    producerCancellation,
                    deadlineCancellation,
                    diagnosticCulture);
        }

        return _diagnostics.NativeFailure(status, source, diagnosticCulture);
    }

    /// <summary>
    /// Takes ownership of the native scene buffer, copies it within the scene
    /// budget, decodes it and localizes its diagnostics.
    /// </summary>
    private MermaidRenderResult MaterializeNativeScene(
        nint rawBuffer,
        string source,
        CancellationToken operationCancellation,
        CultureInfo diagnosticCulture)
    {
        using var nativeBuffer = new MermaidBufferHandle(rawBuffer);
        operationCancellation.ThrowIfCancellationRequested();

        NativeStatus status = NativeMethods.BufferData(nativeBuffer, out nint data, out uint nativeLength);
        if (status != NativeStatus.Success || data == 0 || nativeLength > (uint)_options.Budgets.MaxSceneBytes || nativeLength > (uint)int.MaxValue)
        {
            return status == NativeStatus.BudgetExceeded || nativeLength > (uint)_options.Budgets.MaxSceneBytes
                ? _diagnostics.Result(
                    MermaidRenderStatus.BudgetExceeded,
                    source,
                    "MMR0008",
                    MermaidStringKeys.SceneTooLarge,
                    diagnosticCulture,
                    "The native scene exceeds MaxSceneBytes ({0} bytes).",
                    _options.Budgets.MaxSceneBytes)
                : _diagnostics.NativeFailure(status, source, diagnosticCulture);
        }

        int length = checked((int)nativeLength);
        var managedBuffer = GC.AllocateUninitializedArray<byte>(length);
        CopyNativeBuffer(data, managedBuffer, operationCancellation);
        MermaidSceneDecodeResult decoded = MermaidSceneDecoder.Decode(
            managedBuffer,
            source,
            _options.Budgets,
            operationCancellation);
        if (!decoded.IsSuccess)
        {
            return _diagnostics.Result(
                decoded.Status == MermaidSceneDecodeStatus.BudgetExceeded ? MermaidRenderStatus.BudgetExceeded : MermaidRenderStatus.InvalidScene,
                source,
                "MMR0009",
                MermaidStringKeys.SceneInvalid,
                diagnosticCulture,
                "The native Mermaid scene is invalid.");
        }

        operationCancellation.ThrowIfCancellationRequested();
        MermaidScene scene = _diagnostics.LocalizeSceneDiagnostics(decoded.Scene!, diagnosticCulture);
        return new MermaidRenderResult(
            MermaidRenderStatus.Success,
            scene,
            scene.Diagnostics,
            source);
    }

    private unsafe NativeStatus GetOrCreateEngine(out MermaidEngineHandle? engine)
    {
        lock (_engineSync)
        {
            if (_engine is { IsClosed: false, IsInvalid: false })
            {
                engine = _engine;
                return NativeStatus.Success;
            }

            var engineOptions = new NativeEngineOptions
            {
                StructSize = (uint)Marshal.SizeOf<NativeEngineOptions>(),
                AbiVersion = NativeMethods.PackedAbiVersion,
                MaxWorkingMemoryBytes = checked((ulong)_options.Budgets.MaxWorkingMemoryBytes),
                MaxConcurrentRenders = checked((uint)_options.Budgets.MaxConcurrentRenders),
            };
            byte[] catalog = _fontCatalog.Serialize();
            fixed (byte* catalogPointer = catalog)
            {
                NativeStatus status = NativeMethods.EngineCreate(
                    in engineOptions,
                    catalogPointer,
                    checked((uint)catalog.Length),
                    out nint rawEngine);
                if (status != NativeStatus.Success || rawEngine == 0)
                {
                    engine = null;
                    return status;
                }

                _engine = new MermaidEngineHandle(rawEngine);
                engine = _engine;
                return NativeStatus.Success;
            }
        }
    }

    private void DisposeNativeEngine()
    {
        lock (_engineSync)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void DisposeResources()
    {
        _sceneCache.Dispose();
        DisposeNativeEngine();
    }

    private NativeRenderOptions CreateNativeRenderOptions(uint deadlineMilliseconds) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeRenderOptions>(),
        Layout = (uint)_options.Layout,
        Theme = (uint)_options.Theme,
        MaxSourceBytes = checked((uint)_options.Budgets.MaxSourceBytes),
        MaxNodes = checked((uint)_options.Budgets.MaxNodes),
        MaxEdges = checked((uint)_options.Budgets.MaxEdges),
        MaxDepth = checked((uint)_options.Budgets.MaxDepth),
        MaxLabelBytes = checked((uint)_options.Budgets.MaxLabelBytes),
        MaxSceneBytes = checked((uint)_options.Budgets.MaxSceneBytes),
        DeadlineMilliseconds = deadlineMilliseconds,
    };

    private static void CopyNativeBuffer(nint source, byte[] destination, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < destination.Length; offset += NativeCopyChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(NativeCopyChunkBytes, destination.Length - offset);
            Marshal.Copy(IntPtr.Add(source, offset), destination, offset, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
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
