using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The renderer's entry point to the isolated resvg workers. It composes the
/// scheduler that owns the worker slots with the operations that run on an
/// admitted worker: document open, render and reattachment, raster decode,
/// and the document bindings that close documents and trim caches.
/// </summary>
internal sealed class WorkerPool : IAsyncDisposable, IDisposable
{
    private readonly WorkerRequestFactory _requests;
    private readonly WorkerScheduler _scheduler;
    private readonly WorkerDocumentBindings _bindings;
    private readonly WorkerDocumentOpener _opener;
    private readonly WorkerDocumentRenderer _renderer;
    private readonly WorkerRasterDecoder _rasterDecoder;

    public WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        IWorkerProcessLauncher? launcher = null,
        IWorkerAdmissionObserver? admissionObserver = null)
        : this(options, TimeProvider.System, WorkerEnergySaver.QueryEnergySaverOn, null, null, useWorkerSecurityContext: true,
            new SvgWorkerPolicyTrace(), launcher, admissionObserver)
    {
    }

    internal WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn,
        Func<string, SvgSourceBuffer, SharedMemoryLease> sourceMemoryFactory,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory = null,
        SvgWorkerPolicyTrace? workerPolicyTrace = null,
        IWorkerProcessLauncher? launcher = null)
        : this(options, timeProvider, isEnergySaverOn, sourceMemoryFactory, outputMemoryFactory,
            useWorkerSecurityContext: false, workerPolicyTrace ?? new SvgWorkerPolicyTrace(), launcher, admissionObserver: null)
    {
    }

    private WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn,
        Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory,
        bool useWorkerSecurityContext,
        SvgWorkerPolicyTrace workerPolicyTrace,
        IWorkerProcessLauncher? launcher,
        IWorkerAdmissionObserver? admissionObserver)
    {
        if (useWorkerSecurityContext && (sourceMemoryFactory is not null || outputMemoryFactory is not null))
            throw new ArgumentException("Production mapping factories must use the selected worker's security context.");
        if (!useWorkerSecurityContext && sourceMemoryFactory is null)
            throw new ArgumentNullException(nameof(sourceMemoryFactory));
        outputMemoryFactory = useWorkerSecurityContext
            ? null
            : outputMemoryFactory ?? WorkerSharedMemoryMapping.CreateOutputMemory;
        bool supportsSecondaryHardware = WorkerSchedulingPolicy.CanUseSecondary(
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            energySaverOn: false);
        WorkerPath = ResolveWorkerPath(options);
        _requests = new WorkerRequestFactory(
            options,
            supportsSecondaryHardware
                ? Math.Max(1, options.ParsedResourceCacheBytes / 2)
                : options.ParsedResourceCacheBytes);
        _scheduler = new WorkerScheduler(
            options,
            _requests,
            launcher ?? new WindowsWorkerProcessLauncher(WorkerPath, options),
            timeProvider,
            isEnergySaverOn,
            supportsSecondaryHardware,
            workerPolicyTrace,
            admissionObserver: admissionObserver);
        var textAdmission = new WorkerTextAdmission(_scheduler, _requests, options);
        _bindings = new WorkerDocumentBindings(_scheduler, _requests, options);
        _opener = new WorkerDocumentOpener(_scheduler, textAdmission, _bindings, _requests, options, sourceMemoryFactory);
        _renderer = new WorkerDocumentRenderer(
            _scheduler, textAdmission, _bindings, _requests, options, sourceMemoryFactory, outputMemoryFactory);
        _rasterDecoder = new WorkerRasterDecoder(_scheduler, _requests, options);
    }

    public string WorkerPath { get; }
    public long FontGeneration => _requests.FontGeneration;
    internal long SourceAttachmentCount => _bindings.SourceAttachmentCount;

    public Task WarmUpAsync(CancellationToken cancellationToken) => _scheduler.WarmUpAsync(cancellationToken);

    // This internal operation is consumed once by the renderer. Reuse only
    // the runtime-owned async frame, never the document/source lifetime.
    public ValueTask<WorkerOpenResult> OpenDocumentAsync(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        CancellationToken cancellationToken,
        Action? contentAdmissionGuard = null,
        Action? contentFailureCallback = null,
        MarkdownSvgRenderDemand? demand = null) =>
        _opener.OpenDocumentAsync(
            preflight,
            openRequest,
            cancellationToken,
            contentAdmissionGuard,
            contentFailureCallback,
            demand);

    public Task<WorkerRenderResult> RenderDocumentAsync(
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        int outputLength,
        CancellationToken cancellationToken,
        MarkdownSvgRenderDemand? demand = null) =>
        _renderer.RenderDocumentAsync(
            document,
            preflight,
            openRequest,
            renderRequest,
            outputLength,
            cancellationToken,
            demand);

    internal Task<WorkerRenderResult> DecodeRasterAsync(
        MarkdownRasterDecodeRequest request,
        RasterDecodeGeometry geometry,
        byte[] hash,
        Action contentAdmissionGuard,
        Action contentFailureCallback,
        CancellationToken cancellationToken) =>
        _rasterDecoder.DecodeRasterAsync(
            request,
            geometry,
            hash,
            contentAdmissionGuard,
            contentFailureCallback,
            cancellationToken);

    public void ForgetDocument(WorkerDocumentHandle document) => _bindings.ForgetDocument(document);

    public void AdvanceFontGeneration() => _requests.AdvanceFontGeneration();

    public Task TrimCacheAsync(CancellationToken cancellationToken) => _bindings.TrimCacheAsync(cancellationToken);

    public void InvalidateWorker(long workerInstanceId) => _scheduler.InvalidateWorker(workerInstanceId);

    public void Dispose() => _scheduler.Dispose();

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();

    internal static string ResolveWorkerPath(ResvgMarkdownSvgRendererOptions options)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X86 or Architecture.X64 or Architecture.Arm64))
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ArchitectureMismatch);
        }
        return options.WorkerExecutablePath ??
            Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);
    }
}
