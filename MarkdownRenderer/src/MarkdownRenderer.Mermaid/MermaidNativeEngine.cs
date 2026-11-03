using System.Globalization;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Owns the lazily created native Mermaid engine and runs one native render:
/// cancellation bridging, the pinned render call, and copying, decoding and
/// localizing the native scene buffer within the configured budgets.
/// </summary>
internal sealed class MermaidNativeEngine : IDisposable
{
    private const int NativeCopyChunkBytes = 1024 * 1024;
    private readonly MermaidRenderOptions _options;
    private readonly MermaidFontCatalog _fontCatalog;
    private readonly MermaidRenderDiagnostics _diagnostics;
    private readonly object _engineSync = new();
    private MermaidEngineHandle? _engine;

    internal MermaidNativeEngine(
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        MermaidRenderDiagnostics diagnostics)
    {
        _options = options;
        _fontCatalog = fontCatalog;
        _diagnostics = diagnostics;
    }

    internal MermaidRenderResult RenderNative(
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

            return RenderWithNativeCancellation(
                engine,
                source,
                sourceBytes,
                operationCancellation,
                producerCancellation,
                deadlineCancellation,
                diagnosticCulture,
                nativeDeadlineMilliseconds);
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
    /// Renders through one native cancellation handle that the operation token
    /// signals, materializing the returned buffer or mapping its failure.
    /// </summary>
    private unsafe MermaidRenderResult RenderWithNativeCancellation(
        MermaidEngineHandle engine,
        string source,
        byte[] sourceBytes,
        CancellationToken operationCancellation,
        CancellationToken producerCancellation,
        CancellationToken deadlineCancellation,
        CultureInfo diagnosticCulture,
        uint nativeDeadlineMilliseconds)
    {
        NativeStatus status = NativeMethods.CancellationCreate(out nint rawCancellation);
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

    /// <summary>Releases the native engine; a later render creates a new one.</summary>
    public void Dispose()
    {
        lock (_engineSync)
        {
            _engine?.Dispose();
            _engine = null;
        }
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
}
