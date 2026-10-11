using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Networking.Connectivity;
using Windows.System.Power;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

internal enum MarkdownParsePipelineStage
{
    Begin = 0,
    EngineParseAndCache = 1,
    LegacyParseAndDocument = 2,
    ProgressiveScenePlan = 3,
    StyleRoleDemand = 4,
    SessionTotal = 5,
}

/// <summary>
/// Shares bounded image preparation between document views in one security partition.
/// The host owns this service; controls borrow it and own their document scopes.
/// </summary>
public sealed class MarkdownPerformanceSession : IMarkdownPerformanceSessionInternal, IDisposable, IAsyncDisposable
{
    private readonly object _cacheGate = new();
    // Source bytes and the bookkeeping which owns them have separate limits.
    // Tiny/empty assets must not create an unbounded number of cache entries.
    private static readonly long SourceCacheMetadataBudgetBytes =
        (IntPtr.Size == 4 ? 2L : 4L) * 1024 * 1024;
    private Dictionary<SourceKey, CachedSource> _sourceCache = new(SourceKeyComparer.Instance);
    private LinkedList<CachedSource> _sourceRecency = new();
    private Dictionary<DocumentScope, HashSet<SourceKey>> _documentSourceKeys = new();
    private readonly ConditionalWeakTable<IMarkdownImageResolver, object> _sourceResolverIdentities = new();
    private readonly object _flightGate = new();
    private readonly Dictionary<SourceKey, SharedFetch> _sharedFlights = new(SourceKeyComparer.Instance);
    private readonly SemaphoreSlim _fetchSlots;
    private readonly BoundedMarkdownLayoutScheduler _layoutWorkScheduler;
    private readonly BoundedMarkdownImageActivationScheduler _imageActivationScheduler = new();
    private readonly FairDocumentAdmission _backgroundAdmission;
    private readonly FairDocumentAdmission _cpuPreparationAdmission;
    private readonly FairDocumentAdmission _scenePreparationAdmission;
    internal MarkdownCompletedSceneCache CompletedSceneCache { get; }
    private readonly SourceByteAdmission _sourceByteAdmission;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly bool _memoryPressureSubscribed;
    private readonly object _workGate = new();
    private readonly object _scenePlansGate = new();
    private readonly SnapshotRetirementTracker _scenePlanRetirements = new();
    private readonly Dictionary<object, MarkdownProgressiveScenePlan> _scenePlans =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<object, RetiredDeferredSceneRegistration> _retiredSceneRegistrations = new();
    private readonly object _styleRoleDemandGate = new();
    private readonly ConditionalWeakTable<MarkdownDocument, Task<MarkdownStyleRoleDemandResult>> _styleRoleDemandCache = new();
    private readonly object _documentScopesGate = new();
    private readonly Dictionary<object, PerformanceDocumentState> _documentScopes =
        new(ReferenceEqualityComparer.Instance);
    private TaskCompletionSource<bool>? _workDrained;
    private TaskCompletionSource<bool>? _shutdownCompleted;
    private int _activeWork;
    private long _sourceCacheBytes;
    private long _sourceCacheMetadataBytes;
    private long _sourceCacheEntryIdentity;
    private long _cancellationRetirementFailures;
    private long _sourceCacheHits;
    private long _imageFetches;
    private long _imageFetchMilliseconds;
    private long _imageFetchFailures;
    private long _imageFetchCancellations;
    private long _sourceCacheEvictions;
    private int _pendingImageFetches;
    private int _activeImageFetches;
    private long _cpuPreparations;
    private long _cpuPreparationMilliseconds;
    private long _scenePreparations;
    private long _scenePreparationMilliseconds;
    private int _disposed;
    private EventHandler? _disposedHandlers;

    /// <summary>Creates an opt-in session. Use a distinct session for each account/security partition.</summary>
    public MarkdownPerformanceSession(MarkdownPerformanceOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        CompletedSceneCache = new MarkdownCompletedSceneCache(options.SceneCacheBudgetBytes);
        _layoutWorkScheduler = new BoundedMarkdownLayoutScheduler();
        _lifetimeToken = _lifetime.Token;
        _fetchSlots = new SemaphoreSlim(options.MaxConcurrentImageFetches);
        _backgroundAdmission = new FairDocumentAdmission(
            options.MaxConcurrentImageFetches - options.ReservedVisibleImageFetches);
        _cpuPreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentCpuPreparations);
        _scenePreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentScenePreparations);
        _sourceByteAdmission = new SourceByteAdmission(
            options.MaxInFlightSourceBytes, options.ReservedVisibleSourceBytes);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // The byte-budgeted LRU still applies in headless and unpackaged hosts.
        }
    }

    // Hosts set this only during an explicit audit. Payloads contain only an
    // in-process owner identity, fixed stage ID, and stopwatch ticks—never source
    // content or resource identifiers. A typed callback avoids runtime event
    // discovery and remains compatible with NativeAOT trimming.
    internal Action<int, int, long>? ParseStageDiagnosticRecorder { get; set; }

    // Admission queue depth is sampled only by internal diagnostics/tests. It
    // adds no work to the CPU-preparation hot path.
    internal int PendingCpuPreparations => _cpuPreparationAdmission.PendingRequests;

    // The renderer checks this once per layout request so stopwatch reads and
    // native-layout counting are absent unless a host explicitly enables audit evidence.
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    internal bool IsLayoutDiagnosticCaptureEnabled { get; set; }
#endif

    private static void ValidateOptions(MarkdownPerformanceOptions options)
    {
        if (options.MaxConcurrentImageFetches < 2 ||
            options.MaxConcurrentImageFetches > (IntPtr.Size == 4 ? 8 : 16))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentImageFetches));
        if (options.ReservedVisibleImageFetches < 1 ||
            options.ReservedVisibleImageFetches >= options.MaxConcurrentImageFetches)
            throw new ArgumentOutOfRangeException(nameof(options.ReservedVisibleImageFetches));
        if (options.MaxConcurrentCpuPreparations < 1 ||
            options.MaxConcurrentCpuPreparations > (IntPtr.Size == 4 ? 1 : 2))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentCpuPreparations));
        if (options.MaxConcurrentScenePreparations < 1 ||
            options.MaxConcurrentScenePreparations > (IntPtr.Size == 4 ? 1 : 2))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentScenePreparations));
        if (options.SourceCacheBudgetBytes < 0 ||
            options.SourceCacheBudgetBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.SourceCacheBudgetBytes));
        if (options.SceneCacheBudgetBytes < 0 ||
            options.SceneCacheBudgetBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.SceneCacheBudgetBytes));
        if (options.MaxInFlightSourceBytes < 2 ||
            options.MaxInFlightSourceBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.MaxInFlightSourceBytes));
        if (options.ReservedVisibleSourceBytes < 1 ||
            options.ReservedVisibleSourceBytes >= options.MaxInFlightSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(options.ReservedVisibleSourceBytes));
        if (options.MaxRasterOutputPixels < 1 ||
            options.MaxRasterOutputPixels > (IntPtr.Size == 4 ? 4_194_304 : 8_388_608))
            throw new ArgumentOutOfRangeException(nameof(options.MaxRasterOutputPixels));
        if (options.LookAheadViewports is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options.LookAheadViewports));
    }

    /// <summary>Gets the immutable settings captured at construction.</summary>
    public MarkdownPerformanceOptions Options { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    event EventHandler? IMarkdownPerformanceSessionInternal.Disposed
    {
        add
        {
            bool invokeImmediately;
            lock (_workGate)
            {
                invokeImmediately = _disposed != 0;
                if (!invokeImmediately)
                    _disposedHandlers += value;
            }

            if (invokeImmediately && value is not null)
                NotifyDisposedHandlers(value);
        }
        remove
        {
            lock (_workGate)
                _disposedHandlers -= value;
        }
    }

    internal long ActiveSourceBytes => _sourceByteAdmission.ActiveBytes;

    // Internal evidence for cancellation callback failures. The callbacks are
    // always observed during CTS retirement; this counter lets focused tests
    // verify that failures were not abandoned with a discarded CancelAsync task.
    internal long CancellationRetirementFailures =>
        Interlocked.Read(ref _cancellationRetirementFailures);

    bool IMarkdownPerformanceSessionInternal.IsDisposed => IsDisposed;
    CancellationToken IMarkdownPerformanceSessionInternal.LifetimeToken => _lifetimeToken;
    IDisposable? IMarkdownPerformanceSessionInternal.TryBeginRasterWork()
    {
        return TryBeginWorkLease();
    }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    bool IMarkdownPerformanceSessionInternal.IsLayoutDiagnosticCaptureEnabled =>
        IsLayoutDiagnosticCaptureEnabled;
#endif

    bool IMarkdownPerformanceSessionInternal.TryGetStyleRoleDemandMask(
        MarkdownDocument document,
        CancellationToken cancellationToken,
        out ulong roleDemandMask)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (IsDisposed)
        {
            roleDemandMask = 0;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Task<MarkdownStyleRoleDemandResult>? collection;
        lock (_styleRoleDemandGate)
        {
            if (!_styleRoleDemandCache.TryGetValue(document, out collection) ||
                !collection.IsCompletedSuccessfully)
            {
                roleDemandMask = 0;
                return false;
            }
        }

        MarkdownStyleRoleDemandResult result = collection.GetAwaiter().GetResult();
        roleDemandMask = result.Mask;
        return result.IsComplete;
    }

    private async ValueTask CacheStyleRoleDemandMaskAsync(
        MarkdownDocument document,
        CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested)
            return;

        Task<MarkdownStyleRoleDemandResult> collection;
        lock (_styleRoleDemandGate)
        {
            if (!_styleRoleDemandCache.TryGetValue(document, out collection!))
            {
                // Start once per immutable document. The session cache uses a
                // weak key, and the control only reads completed results.
                collection = Task.Run(
                    () => MarkdownStyleRoleDemandCollector.Collect(document));
                _styleRoleDemandCache.Add(document, collection);
            }
        }

        await collection.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long StartParseStageTiming(bool enabled) =>
        enabled ? Stopwatch.GetTimestamp() : 0;

    private static void RecordParseStageTiming(
        Action<int, int, long>? recorder,
        int ownerIdentity,
        MarkdownParsePipelineStage stage,
        long startedTimestamp)
    {
        if (recorder is null)
            return;

        long elapsedTicks = stage == MarkdownParsePipelineStage.Begin
            ? 0
            : Stopwatch.GetTimestamp() - startedTimestamp;
        recorder(ownerIdentity, (int)stage, elapsedTicks);
    }

    ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        IMarkdownPerformanceSessionInternal.ParseAndPrepareDocumentAsync(
        MarkdownEngine? engine,
        MarkdownRenderer.Document.MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        ArgumentNullException.ThrowIfNull(legacyRegistry);
        cancellationToken.ThrowIfCancellationRequested();

        // Parsing may create and later retire a progressive scene plan. Count
        // the complete parse/plan transaction as admitted work so shutdown
        // cannot perform its final retirement drain before that transaction
        // registers cleanup. Calls made after shutdown retain the historical
        // eager fallback, but do not touch session-owned plan/cache state.
        if (!TryRegisterWork())
        {
            return ParseForDisposedSessionAsync(
                engine,
                document,
                source,
                legacyRegistry,
                documentOwner,
                cancellationToken);
        }

        return ParseAndPrepareRegisteredDocumentAsync(
            engine,
            document,
            source,
            legacyRegistry,
            documentOwner,
            cancellationToken);
    }

    private async ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        ParseAndPrepareRegisteredDocumentAsync(
        MarkdownEngine? engine,
        MarkdownRenderer.Document.MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ParseAndPrepareDocumentCoreAsync(
                engine,
                document,
                source,
                legacyRegistry,
                documentOwner,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RetireWork();
        }
    }

    private async ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        ParseAndPrepareDocumentCoreAsync(
        MarkdownEngine? engine,
        MarkdownRenderer.Document.MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        Action<int, int, long>? parseStageRecorder = ParseStageDiagnosticRecorder;
        bool captureParseStageTimings = parseStageRecorder is not null;
        int ownerIdentity = captureParseStageTimings
            ? RuntimeHelpers.GetHashCode(documentOwner)
            : 0;
        long sessionStarted = StartParseStageTiming(captureParseStageTimings);
        if (captureParseStageTimings)
        {
            RecordParseStageTiming(
                parseStageRecorder,
                ownerIdentity,
                MarkdownParsePipelineStage.Begin,
                startedTimestamp: 0);
        }

        bool parseProgressively = engine is not null && Options.DeferOffscreenScenes && !IsDisposed;
        if (document is null && engine is not null)
        {
            long engineParseStarted = StartParseStageTiming(captureParseStageTimings);
            document = parseProgressively
                ? await engine.ParseForProgressivePresentationAsync(source, cancellationToken).ConfigureAwait(false)
                : await engine.ParseAsync(source, cancellationToken).ConfigureAwait(false);
            if (captureParseStageTimings)
            {
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.EngineParseAndCache,
                    engineParseStarted);
            }
        }
        else if (document is null && documentOwner is IMarkdownPerformanceLegacyParser legacyParser)
        {
            long legacyParseStarted = StartParseStageTiming(captureParseStageTimings);
            MarkdownRenderer.Parsing.ParsedMarkdown? parsed = await legacyParser.ParseLegacyMarkdownAsync(
                source ?? string.Empty,
                legacyRegistry,
                cancellationToken).ConfigureAwait(false);
            if (parsed is not null)
            {
                document = await Task.Run(
                    () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                        parsed.SourceText,
                        parsed.Document,
                        diagnostics: [],
                    cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            if (captureParseStageTimings)
            {
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.LegacyParseAndDocument,
                    legacyParseStarted);
            }
        }

        if (document is null)
        {
            await ((IMarkdownPerformanceSessionInternal)this)
                .ReleaseDeferredSceneDocument(documentOwner)
                .ConfigureAwait(false);
            if (captureParseStageTimings)
            {
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.SessionTotal,
                    sessionStarted);
            }
            return null;
        }

        if (engine is null || !parseProgressively)
        {
            await ((IMarkdownPerformanceSessionInternal)this)
                .ReleaseDeferredSceneDocument(documentOwner)
                .ConfigureAwait(false);
            if (MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
            {
                if (engine is not null)
                {
                    long engineParseStarted = StartParseStageTiming(captureParseStageTimings);
                    document = await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);
                    if (captureParseStageTimings)
                    {
                        RecordParseStageTiming(
                            parseStageRecorder,
                            ownerIdentity,
                            MarkdownParsePipelineStage.EngineParseAndCache,
                            engineParseStarted);
                    }
                }
                else if (documentOwner is IMarkdownPerformanceLegacyParser legacyParser)
                {
                    long legacyParseStarted = StartParseStageTiming(captureParseStageTimings);
                    MarkdownRenderer.Parsing.ParsedMarkdown? parsed = await legacyParser.ParseLegacyMarkdownAsync(
                        document.Source,
                        legacyRegistry,
                        cancellationToken).ConfigureAwait(false);
                    if (parsed is not null)
                    {
                        document = await Task.Run(
                            () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                                parsed.SourceText,
                                parsed.Document,
                                diagnostics: [],
                                cancellationToken),
                                CancellationToken.None).ConfigureAwait(false);
                    }
                    if (captureParseStageTimings)
                    {
                        RecordParseStageTiming(
                            parseStageRecorder,
                            ownerIdentity,
                            MarkdownParsePipelineStage.LegacyParseAndDocument,
                            legacyParseStarted);
                    }
                }
            }
            long roleDemandStarted = StartParseStageTiming(captureParseStageTimings);
            await CacheStyleRoleDemandMaskAsync(document, cancellationToken).ConfigureAwait(false);
            if (captureParseStageTimings)
            {
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.StyleRoleDemand,
                    roleDemandStarted);
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.SessionTotal,
                    sessionStarted);
            }
            return document;
        }

        long scenePlanStarted = StartParseStageTiming(captureParseStageTimings);
        await PrepareProgressiveSceneDocumentAsync(
            document,
            documentOwner,
            cancellationToken).ConfigureAwait(false);
        if (captureParseStageTimings)
        {
            RecordParseStageTiming(
                parseStageRecorder,
                ownerIdentity,
                MarkdownParsePipelineStage.ProgressiveScenePlan,
                scenePlanStarted);
        }

        // If the borrowed session is disposed while parse/plan construction is
        // in flight, never leave this control with a deferred fallback and no
        // scheduler. Reparse eagerly; disposal remains safe for concurrent
        // controls because ParseAsync does not depend on the session lifetime.
        if (IsDisposed && MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
        {
            await ((IMarkdownPerformanceSessionInternal)this)
                .ReleaseDeferredSceneDocument(documentOwner)
                .ConfigureAwait(false);
            long engineParseStarted = StartParseStageTiming(captureParseStageTimings);
            document = await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);
            if (captureParseStageTimings)
            {
                RecordParseStageTiming(
                    parseStageRecorder,
                    ownerIdentity,
                    MarkdownParsePipelineStage.EngineParseAndCache,
                    engineParseStarted);
            }
        }

        long progressiveRoleDemandStarted = StartParseStageTiming(captureParseStageTimings);
        await CacheStyleRoleDemandMaskAsync(document, cancellationToken).ConfigureAwait(false);
        if (captureParseStageTimings)
        {
            RecordParseStageTiming(
                parseStageRecorder,
                ownerIdentity,
                MarkdownParsePipelineStage.StyleRoleDemand,
                progressiveRoleDemandStarted);
            RecordParseStageTiming(
                parseStageRecorder,
                ownerIdentity,
                MarkdownParsePipelineStage.SessionTotal,
                sessionStarted);
        }
        return document;
    }

    private static async ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        ParseForDisposedSessionAsync(
        MarkdownEngine? engine,
        MarkdownRenderer.Document.MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            if (engine is not null)
            {
                return await engine.ParseAsync(source, cancellationToken).ConfigureAwait(false);
            }

            if (documentOwner is IMarkdownPerformanceLegacyParser legacyParser)
            {
                MarkdownRenderer.Parsing.ParsedMarkdown? parsed = await legacyParser
                    .ParseLegacyMarkdownAsync(source ?? string.Empty, legacyRegistry, cancellationToken)
                    .ConfigureAwait(false);
                if (parsed is null)
                    return null;

                return await Task.Run(
                    () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                        parsed.SourceText,
                        parsed.Document,
                        diagnostics: [],
                        cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return null;
        }

        if (!MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
            return document;

        if (engine is not null)
            return await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);

        if (documentOwner is not IMarkdownPerformanceLegacyParser legacyDocumentParser)
            return document;

        MarkdownRenderer.Parsing.ParsedMarkdown? reparsed = await legacyDocumentParser
            .ParseLegacyMarkdownAsync(document.Source, legacyRegistry, cancellationToken)
            .ConfigureAwait(false);
        if (reparsed is null)
            return document;

        return await Task.Run(
            () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                reparsed.SourceText,
                reparsed.Document,
                diagnostics: [],
                cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    Task<T> IMarkdownPerformanceSessionInternal.ScheduleLayoutWorkAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken)
    {
        if (work is null)
            return Task.FromException<T>(new ArgumentNullException(nameof(work)));
        if (!TryRegisterWork())
            return CreateLayoutWorkFailure<T>(new OperationCanceledException(cancellationToken));

        CancellationTokenSource? linked = null;
        Task<T>? scheduled = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeToken);
            scheduled = _layoutWorkScheduler.ScheduleAsync(priority, work, linked.Token);
        }
        catch (Exception exception)
        {
            linked?.Dispose();
            RetireWork();
            return CreateLayoutWorkFailure<T>(exception);
        }

        var completion = new LayoutWorkCompletion(this, linked);
        try
        {
            _ = scheduled.ContinueWith(
                static (_, state) => ((LayoutWorkCompletion)state!).Complete(),
                completion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            // Retain the original await-and-retire behavior if completion
            // tracking itself cannot be attached.
            return AwaitLayoutWorkAndCompleteAsync(scheduled, completion);
        }

        return scheduled;
    }

    bool IMarkdownPerformanceSessionInternal.TryScheduleImageActivation(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(startAsyncWork);
        ArgumentNullException.ThrowIfNull(onNotStarted);
        if (cancellationToken.IsCancellationRequested || !TryRegisterWork())
            return false;

        CancellationTokenSource? linked = null;
        ImageActivationCompletion? completion = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeToken);
            ImageActivationCompletion workCompletion = new(this, linked, onNotStarted);
            completion = workCompletion;
            bool scheduled = _imageActivationScheduler.TrySchedule(
                demand,
                () =>
                {
                    try
                    {
                        startAsyncWork();
                    }
                    finally
                    {
                        workCompletion.Complete(started: true);
                    }
                },
                () => workCompletion.Complete(started: false),
                linked.Token);
            if (scheduled)
                return true;

            completion.Complete(started: false);
            return false;
        }
        catch (Exception exception)
        {
            if (completion is not null)
                completion.Complete(started: false);
            else
            {
                linked?.Dispose();
                RetireWork();
            }

            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation could not be scheduled: {exception.Message}");
            return false;
        }
    }

    private sealed class ImageActivationCompletion(
        MarkdownPerformanceSession session,
        CancellationTokenSource linked,
        Action onNotStarted)
    {
        private int _completed;

        internal void Complete(bool started)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            try
            {
                if (!started)
                    onNotStarted();
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Image activation retirement failed: {exception.Message}");
            }
            finally
            {
                try
                {
                    linked.Dispose();
                }
                finally
                {
                    session.RetireWork();
                }
            }
        }
    }

    private static Task<T> CreateLayoutWorkFailure<T>(Exception exception)
    {
        if (exception is not OperationCanceledException canceled)
            return Task.FromException<T>(exception);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetCanceled(canceled.CancellationToken);
        return completion.Task;
    }

    private static async Task<T> AwaitLayoutWorkAndCompleteAsync<T>(
        Task<T> scheduled,
        LayoutWorkCompletion completion)
    {
        try
        {
            return await scheduled.ConfigureAwait(false);
        }
        finally
        {
            completion.Complete();
        }
    }

    private sealed class LayoutWorkCompletion(
        MarkdownPerformanceSession session,
        CancellationTokenSource linked)
    {
        private int _completed;

        internal void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            try
            {
                linked.Dispose();
            }
            finally
            {
                session.RetireWork();
            }
        }
    }

    private async ValueTask PrepareProgressiveSceneDocumentAsync(
        MarkdownRenderer.Document.MarkdownDocument document,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        MarkdownProgressiveScenePlan? current;
        lock (_scenePlansGate)
        {
            _scenePlans.TryGetValue(documentOwner, out current);
            if (current?.IsFor(document) == true)
                return;
        }

        MarkdownProgressiveScenePlan? candidate = IsDisposed || cancellationToken.IsCancellationRequested
            ? null
            : await MarkdownProgressiveScenePlan.TryCreateAsync(
                document,
                this,
                documentOwner,
                cancellationToken).ConfigureAwait(false);
        MarkdownProgressiveScenePlan? retired = null;
        ScenePlanRetirementReservation? retirementReservation = null;
        lock (_scenePlansGate)
        {
            if (IsDisposed || cancellationToken.IsCancellationRequested)
            {
                retired = candidate;
            }
            else if (_scenePlans.TryGetValue(documentOwner, out current) && current.IsFor(document))
            {
                retired = candidate;
            }
            else
            {
                if (current is not null)
                    retired = current;
                if (candidate is null)
                    _scenePlans.Remove(documentOwner);
                else
                    _scenePlans[documentOwner] = candidate;
            }

            // Publish the retirement barrier before dropping the registration
            // lock. DisposeAsync may otherwise detach this plan, run its final
            // drain, and complete before this thread records the cleanup task.
            retirementReservation = ReserveScenePlanRetirementNoLock(retired);
        }

        await StartScenePlanRetirement(
            retirementReservation,
            static plan => plan.Retire()).ConfigureAwait(false);
    }

    bool IMarkdownPerformanceSessionInternal.TryGetDeferredSceneResult(
        object documentOwner,
        string marker,
        out MarkdownContentFragment? result)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        if (plan is not null && plan.TryGetResult(marker, out result))
            return true;
        result = null;
        return false;
    }

    bool IMarkdownPerformanceSessionInternal.TryGetDeferredSceneResult(
        object documentOwner, string marker,
        out MarkdownContentFragment? result, out object? projectionIdentity)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.TryGetResult(marker, out result, out projectionIdentity);
        }
        result = null;
        projectionIdentity = null;
        return false;
    }

    bool IMarkdownPerformanceSessionInternal.HasRegisteredDeferredSceneWork(object documentOwner)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.HasRegisteredWork;
            if (_retiredSceneRegistrations.TryGetValue(
                    documentOwner,
                    out RetiredDeferredSceneRegistration? retired))
                return retired.HasRegisteredWork;
        }
        return false;
    }

    bool IMarkdownPerformanceSessionInternal.IsRegisteredDeferredSceneMarker(
        object documentOwner,
        string marker)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.IsRegisteredMarker(marker);
            if (_retiredSceneRegistrations.TryGetValue(
                    documentOwner,
                    out RetiredDeferredSceneRegistration? retired))
                return retired.Contains(marker);
        }
        return false;
    }

#pragma warning disable MR1001 // The optional scene policy integrates with the compatibility control surface.
    MarkdownVisibleSceneReadiness IMarkdownPerformanceSessionInternal.GetVisibleDeferredSceneReadiness(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        if (expectedGeneration <= 0 ||
            !MarkdownProgressiveScenePlan.IsFiniteNonEmptyRect(viewport) ||
            !IsDeferredSceneReadinessCurrent(
                owner,
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision))
        {
            return MarkdownVisibleSceneReadiness.NotReady;
        }

        // A failed geometry rollback is not a transient reader collision. The
        // control replaces this snapshot; reporting Busy here would ask UIA to
        // keep retrying against geometry that the snapshot has explicitly
        // stopped exposing.
        if (expectedSnapshot.GeometryRecoveryRequired)
            return MarkdownVisibleSceneReadiness.Failed;
        if (expectedSnapshot.IsRetiringOrDisposed)
            return MarkdownVisibleSceneReadiness.NotReady;
        if (!expectedSnapshot.TryBeginPaint())
        {
            if (expectedSnapshot.GeometryRecoveryRequired)
                return MarkdownVisibleSceneReadiness.Failed;
            if (expectedSnapshot.IsRetiringOrDisposed ||
                !IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }
            return MarkdownVisibleSceneReadiness.Busy;
        }

        try
        {
            if (expectedSnapshot.GeometryRecoveryRequired)
                return MarkdownVisibleSceneReadiness.Failed;
            if (expectedSnapshot.IsRetiringOrDisposed)
                return MarkdownVisibleSceneReadiness.NotReady;
            if (!IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            LazyLayoutBand visibleBand = LazyLayoutBand.FromViewport(
                viewport.Top,
                viewport.Height,
                overscan: 0);
            // Failure is a terminal status, not render readiness. Probe under
            // the same validated lease before the strict measured-band gate.
            if (expectedSnapshot.HasFailedScenesInBandUnderPaint(visibleBand))
                return MarkdownVisibleSceneReadiness.Failed;
            if (!expectedSnapshot.IsBandMeasured(visibleBand) ||
                !expectedSnapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                    viewport.Top,
                    viewport.Bottom,
                    out IReadOnlyList<BlockBox> visibleBlocks,
                    out IReadOnlyList<int> visibleOrdinals,
                    out LayoutPlacementGeneration placements,
                    out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            bool scanVerified = MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
                visibleBlocks,
                visibleOrdinals,
                placements,
                topLevelOrdinalByBlockIndex,
                viewport.Top,
                viewport.Bottom,
                viewport,
                expectedSnapshot.VectorSceneResidencyPolicy,
                out IReadOnlyList<string> visibleMarkerKeys,
                out bool visibleSceneResourcesReady);
            if (!scanVerified ||
                !IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            bool pending = MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
                // The exact visible marker identities are authoritative here;
                // keep checking them even if aggregate plan bookkeeping says
                // no work remains.
                hasRegisteredWork: true,
                visibleBandVerified: true,
                visiblePlaceholderKeys: visibleMarkerKeys,
                isRegisteredMarker: marker =>
                    ((IMarkdownPerformanceSessionInternal)this).IsRegisteredDeferredSceneMarker(owner, marker));
            return !pending &&
                visibleSceneResourcesReady &&
                IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision)
                    ? MarkdownVisibleSceneReadiness.Ready
                    : MarkdownVisibleSceneReadiness.NotReady;
        }
        finally
        {
            expectedSnapshot.EndPaint();
        }
    }

    private bool IsDeferredSceneReadinessCurrent(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision) =>
        owner.IsDeferredSceneSnapshotCurrent(
            expectedSnapshot,
            expectedGeneration,
            expectedLayoutRevision) &&
        ReferenceEquals(owner.PerformanceSession, this);
#pragma warning restore MR1001

    IMarkdownVectorSceneResidencyPolicy? IMarkdownPerformanceSessionInternal.CreateVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        object? documentOwner)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        MarkdownProgressiveScenePlan? plan = null;
        if (documentOwner is not null)
            lock (_scenePlansGate)
                _scenePlans.TryGetValue(documentOwner, out plan);
        if (!Options.DeferOffscreenScenes || IsDisposed)
            return null;

        // Plain, bounded inline trees have no vector resources for a policy to
        // own. Avoid creating an otherwise empty optional policy so Core can
        // build a private lazy-placement candidate without referencing this
        // optional assembly. Any registered scene plan or unknown tree keeps
        // the ordinary policy and remains ineligible for that fast path.
        if (plan is null && MarkdownVectorSceneResidencyPolicy.CanOmitForPrivatePlacement(blocks))
            return null;

        return new MarkdownVectorSceneResidencyPolicy(blocks, plan, VectorNativeResourceAdmission.Shared);
    }

    Task<bool> IMarkdownPerformanceSessionInternal.PrepareDeferredSceneBandAsync(
        object documentOwner,
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        CancellationToken cancellationToken)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        return plan is not null && !IsDisposed
            ? Task.Run(() => plan.RebindBandAsync(snapshot, band, cancellationToken), CancellationToken.None)
            : Task.FromResult(false);
    }

    IMarkdownImageResidencyPolicy? IMarkdownPerformanceSessionInternal.CreateImageResidencyPolicy(
        Action requestReconcile)
    {
        ArgumentNullException.ThrowIfNull(requestReconcile);
        return Options.ReleaseOffscreenImageResources && !IsDisposed
            ? new MarkdownImageResidencyPolicy(requestReconcile)
            : null;
    }

    /// <summary>
    /// Returns diagnostics reported by deferred scenes in the supplied
    /// immutable document. They become available after visible scene work is
    /// published and remain separate from the document's eager diagnostics.
    /// </summary>
    public IReadOnlyList<MarkdownDiagnostic> GetDeferredSceneDiagnostics(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MarkdownProgressiveScenePlan[] plans;
        lock (_scenePlansGate)
            plans = _scenePlans.Values.Where(plan => plan.IsFor(document)).ToArray();
        if (plans.Length == 0)
            return Array.Empty<MarkdownDiagnostic>();
        if (plans.Length == 1)
            return plans[0].Diagnostics;

        var diagnostics = new HashSet<MarkdownDiagnostic>();
        foreach (MarkdownProgressiveScenePlan plan in plans)
            foreach (MarkdownDiagnostic diagnostic in plan.Diagnostics)
                diagnostics.Add(diagnostic);
        if (diagnostics.Count == 0)
            return Array.Empty<MarkdownDiagnostic>();

        List<MarkdownDiagnostic> ordered = diagnostics.ToList();
        ordered.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Code, right.Code);
        });
        return Array.AsReadOnly(ordered.ToArray());
    }

    Task IMarkdownPerformanceSessionInternal.AdvanceDeferredSceneGeneration(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        ScenePlanRetirementReservation? reservation;
        lock (_scenePlansGate)
        {
            _scenePlans.TryGetValue(documentOwner, out plan);
            reservation = ReserveScenePlanRetirementNoLock(plan);
        }

        return StartScenePlanRetirement(
            reservation,
            static current => current.AdvanceGeneration());
    }

    Task IMarkdownPerformanceSessionInternal.ReleaseDeferredSceneDocument(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        ScenePlanRetirementReservation? reservation;
        lock (_scenePlansGate)
        {
            _retiredSceneRegistrations.Remove(documentOwner);
            _scenePlans.Remove(documentOwner, out plan);
            reservation = ReserveScenePlanRetirementNoLock(plan);
        }
        return StartScenePlanRetirement(reservation, static current => current.Retire());
    }

    bool IMarkdownPerformanceSessionInternal.ScheduleDeferredScenes(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        return plan is not null && plan.ScheduleVisible(documentOwner);
    }

    private void ReleaseAllProgressiveScenePlans()
    {
        ScenePlanRetirementReservation[] reservations;
        lock (_scenePlansGate)
        {
            var pending = new List<ScenePlanRetirementReservation>(_scenePlans.Count);
            foreach (KeyValuePair<object, MarkdownProgressiveScenePlan> entry in _scenePlans)
            {
                string[] markers = entry.Value.GetRegisteredMarkerSnapshot();
                if (markers.Length != 0)
                {
                    _retiredSceneRegistrations.AddOrUpdate(
                        entry.Key,
                        new RetiredDeferredSceneRegistration(markers));
                }

                ScenePlanRetirementReservation? reservation =
                    ReserveScenePlanRetirementNoLock(entry.Value);
                if (reservation is not null)
                    pending.Add(reservation);
            }
            _scenePlans.Clear();
            reservations = pending.ToArray();
        }

        foreach (ScenePlanRetirementReservation reservation in reservations)
            _ = StartScenePlanRetirement(reservation, static plan => plan.Retire());
    }

    private ScenePlanRetirementReservation? ReserveScenePlanRetirementNoLock(
        MarkdownProgressiveScenePlan? plan)
    {
        if (plan is null)
            return null;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _scenePlanRetirements.Track(completion.Task);
        return new ScenePlanRetirementReservation(plan, completion);
    }

    private static Task StartScenePlanRetirement(
        ScenePlanRetirementReservation? reservation,
        Func<MarkdownProgressiveScenePlan, Task> startRetirement)
    {
        if (reservation is null)
            return Task.CompletedTask;

        Task retirement;
        try
        {
            // Plan cancellation/disposal can take locks and invoke callback
            // completion paths, so never run it while holding _scenePlansGate.
            retirement = startRetirement(reservation.Plan);
        }
        catch (Exception exception)
        {
            CompleteScenePlanRetirementFailure(reservation.Completion, exception);
            return reservation.Completion.Task;
        }

        _ = CompleteScenePlanRetirementReservationAsync(retirement, reservation.Completion);
        return reservation.Completion.Task;
    }

    private static async Task CompleteScenePlanRetirementReservationAsync(
        Task retirement,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await retirement.ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            CompleteScenePlanRetirementFailure(completion, exception);
        }
    }

    private static void CompleteScenePlanRetirementFailure(
        TaskCompletionSource<bool> completion,
        Exception exception)
    {
        if (exception is OperationCanceledException canceled)
            completion.TrySetCanceled(canceled.CancellationToken);
        else
            completion.TrySetException(exception);
    }

    private sealed class ScenePlanRetirementReservation(
        MarkdownProgressiveScenePlan plan,
        TaskCompletionSource<bool> completion)
    {
        internal MarkdownProgressiveScenePlan Plan { get; } = plan;
        internal TaskCompletionSource<bool> Completion { get; } = completion;
    }

    private sealed class RetiredDeferredSceneRegistration(string[] markers)
    {
        internal bool HasRegisteredWork => markers.Length != 0;

        internal bool Contains(string marker) =>
            !string.IsNullOrEmpty(marker) &&
            Array.BinarySearch(markers, marker, StringComparer.Ordinal) >= 0;
    }

    private void ReleaseAllDocumentScopes()
    {
        DocumentScope[] scopes;
        lock (_documentScopesGate)
        {
            scopes = _documentScopes.Values.Select(static state => state.Scope).ToArray();
            _documentScopes.Clear();
        }
        foreach (DocumentScope scope in scopes)
            scope.Dispose();
    }

    long IMarkdownPerformanceSessionInternal.BeginRasterPreparation(
        int sourceBytes, int sourceWidth, int sourceHeight) =>
        MarkdownRasterPreparationEventSource.Log.Begin(sourceBytes, sourceWidth, sourceHeight);

    void IMarkdownPerformanceSessionInternal.RecordRasterPreparationStage(
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long elapsedStopwatchTicks) =>
        MarkdownRasterPreparationEventSource.Log.RecordStage(
            preparationId, stage, elapsedStopwatchTicks);

    IMarkdownPerformanceDocumentScope IMarkdownPerformanceSessionInternal.OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => OpenDocument(resolver, context);

    IMarkdownPerformanceDocumentScope?
        IMarkdownPerformanceSessionInternal.PrepareDocumentScope(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) =>
        PrepareDocumentScope(
            documentOwner,
            source,
            registryRevision,
            document,
            safeHtmlPolicy,
            resolver,
            context);

    void IMarkdownPerformanceSessionInternal.ReleaseDocumentScope(object documentOwner)
        => ReleaseDocumentScope(documentOwner);

    ValueTask<IDisposable> IMarkdownPerformanceSessionInternal.EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, exclusive, cancellationToken);

    ValueTask<IMarkdownScenePreparationLease> IMarkdownPerformanceSessionInternal.EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterScenePreparationAsync(documentOwner, cancellationToken);

    /// <summary>Returns aggregate counts without source URLs or content.</summary>
    public MarkdownPerformanceSnapshot GetSnapshot()
    {
        long retainedBytes;
        lock (_cacheGate)
            retainedBytes = _sourceCacheBytes;
        return new MarkdownPerformanceSnapshot(
            retainedBytes,
            Interlocked.Read(ref _sourceCacheHits),
            Interlocked.Read(ref _imageFetches),
            Interlocked.Read(ref _imageFetchMilliseconds),
            Interlocked.Read(ref _imageFetchFailures),
            Interlocked.Read(ref _imageFetchCancellations),
            Interlocked.Read(ref _sourceCacheEvictions),
            Volatile.Read(ref _pendingImageFetches),
            Volatile.Read(ref _activeImageFetches),
            Interlocked.Read(ref _cpuPreparations),
            Interlocked.Read(ref _cpuPreparationMilliseconds),
            Interlocked.Read(ref _scenePreparations),
            Interlocked.Read(ref _scenePreparationMilliseconds),
            _sourceByteAdmission.ActiveBytes,
            _sourceByteAdmission.PeakActiveBytes,
            _sourceByteAdmission.PendingRequests,
            CompletedSceneCache.RetainedWeightBytes,
            CompletedSceneCache.Hits,
            CompletedSceneCache.Evictions);
    }

    /// <summary>Releases source and completed-scene cache retention; active layouts and visible handoffs are unaffected.</summary>
    public void Trim()
    {
        lock (_cacheGate)
        {
            ResetSourceCacheNoLock();
        }
        CompletedSceneCache.Trim();
    }

    private void ResetSourceCacheNoLock()
    {
        // Swap private roots instead of clearing high-water backing arrays on
        // a memory-pressure/UI callback. No external borrower owns these roots.
        _sourceCache = new(SourceKeyComparer.Instance);
        _sourceRecency = new();
        _documentSourceKeys = new();
        _sourceCacheBytes = 0;
        _sourceCacheMetadataBytes = 0;
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args) => Trim();

    internal DocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        lock (_workGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new DocumentScope(this, resolver, context);
        }
    }

    private IMarkdownPerformanceDocumentScope? PrepareDocumentScope(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        DocumentScope? retired = null;
        DocumentScope scope;
        bool startPrefetch = false;
        lock (_documentScopesGate)
        {
            if (IsDisposed)
                return null;

            if (_documentScopes.TryGetValue(documentOwner, out PerformanceDocumentState? current) &&
                string.Equals(current.Source, source, StringComparison.Ordinal) &&
                current.RegistryRevision == registryRevision &&
                current.Scope.Matches(this, resolver, context))
            {
                scope = current.Scope;
            }
            else
            {
                if (current is not null)
                    retired = current.Scope;
                try
                {
                    scope = OpenDocument(resolver, context);
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }

                current = new PerformanceDocumentState(source, registryRevision, scope);
                _documentScopes[documentOwner] = current;
            }

            if (Options.PrefetchDocumentImages && !current.PrefetchStarted)
            {
                current.PrefetchStarted = true;
                startPrefetch = true;
            }
        }

        retired?.Dispose();
        if (startPrefetch)
        {
            // The collector can finish synchronously, and PrefetchAsync may run
            // resolver policy checks before its first incomplete await. Keep the
            // entire speculative path off the caller's (usually UI) thread.
            _ = Task.Run(() => PrefetchDocumentImagesObservedAsync(
                document, safeHtmlPolicy, scope));
        }
        return scope;
    }

    private void ReleaseDocumentScope(object documentOwner)
    {
        DocumentScope? scope;
        lock (_documentScopesGate)
        {
            if (!_documentScopes.Remove(documentOwner, out PerformanceDocumentState? state))
                return;
            scope = state.Scope;
        }
        scope.Dispose();
    }

    private static async Task PrefetchDocumentImagesObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownPerformanceDocumentScope scope)
    {
        try
        {
            CancellationToken cancellationToken = scope.CancellationToken;
            IReadOnlyList<string> sources = MarkdownImagePrefetchSourceCollector.Collect(
                document, safeHtmlPolicy, cancellationToken);
            await scope.PrefetchAsync(sources, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRenderer] performance prefetch failed: {exception.Message}");
        }
    }

    internal ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, exclusive: false, cancellationToken);

    internal async ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        RegisterWork();
        IDisposable? admissionLease = null;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeToken);
            admissionLease = await _cpuPreparationAdmission.EnterAsync(
                documentOwner, exclusive, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            CpuPreparationLease lease = new(
                admissionLease, this, Stopwatch.GetTimestamp());
            admissionLease = null;
            Interlocked.Increment(ref _cpuPreparations);
            return lease;
        }
        catch
        {
            try
            {
                admissionLease?.Dispose();
            }
            finally
            {
                RetireWork();
            }
            throw;
        }
    }

    async ValueTask<IDisposable> IMarkdownPerformanceSessionInternal.ReserveImageSourceBytesAsync(
        object documentOwner,
        long bytes,
        CancellationToken cancellationToken) =>
        await ReserveSourceByteLeaseAsync(
                documentOwner,
                bytes,
                background: false,
                cancellationToken: cancellationToken,
                requestCancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

    private async ValueTask<IDisposable> ReserveSourceByteLeaseAsync(
        object documentOwner,
        long bytes,
        bool background,
        CancellationToken cancellationToken,
        CancellationToken requestCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        if (background && bytes > _sourceByteAdmission.BackgroundCapacity)
            throw new MarkdownImageSourceDeferredException();

        RegisterWork();
        IDisposable? admissionLease = null;
        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, requestCancellationToken, _lifetimeToken);
            admissionLease = await _sourceByteAdmission.EnterAsync(
                    documentOwner, bytes, background, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();

            var result = new SessionSourceByteLease(this, admissionLease, linked);
            admissionLease = null;
            linked = null;
            return result;
        }
        catch
        {
            if (admissionLease is not null)
            {
                try { admissionLease.Dispose(); }
                catch (Exception cleanupException)
                {
                    LogLeaseCleanupFailure("unpublished source-byte admission", cleanupException);
                }
            }
            if (linked is not null)
            {
                try { linked.Dispose(); }
                catch (Exception cleanupException)
                {
                    LogLeaseCleanupFailure("unpublished source-byte cancellation", cleanupException);
                }
            }
            RetireWork();
            throw;
        }
    }

    internal async ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken)
    {
        RegisterWork();
        CancellationTokenSource? linked = null;
        IDisposable? admissionLease = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeToken);
            admissionLease = await _scenePreparationAdmission.EnterAsync(
                documentOwner, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ScenePreparationLease lease = new(
                admissionLease, linked, this, Stopwatch.GetTimestamp());
            admissionLease = null;
            linked = null;
            Interlocked.Increment(ref _scenePreparations);
            return lease;
        }
        catch
        {
            try
            {
                admissionLease?.Dispose();
            }
            finally
            {
                try
                {
                    linked?.Dispose();
                }
                finally
                {
                    RetireWork();
                }
            }
            throw;
        }
    }

    // The progressive scene coordinator holds the admission lease until its
    // dispatcher publication is acknowledged. Keep that queue latency out of
    // the scene-preparation metric by recording the completed preparation
    // interval before enqueueing publication. Other callers retain the
    // historical dispose-time measurement.
    internal static void CompleteScenePreparationTiming(IMarkdownScenePreparationLease? lease)
    {
        if (lease is ScenePreparationLease sceneLease)
            sceneLease.CompletePreparationTiming();
    }

    private void RegisterWork()
    {
        if (!TryRegisterWork())
            throw new OperationCanceledException(_lifetimeToken);
    }

    private RasterWorkLease? TryBeginWorkLease()
    {
        // Allocate first so even an allocation failure cannot strand the
        // active-work count. The returned lease is idempotent.
        RasterWorkLease lease = new(this);
        return TryRegisterWork() ? lease : null;
    }

    private bool TryRegisterWork()
    {
        lock (_workGate)
        {
            if (_disposed != 0)
                return false;
            _activeWork++;
            return true;
        }
    }

    private void RetireWork()
    {
        lock (_workGate)
        {
            if (--_activeWork == 0)
                _workDrained?.TrySetResult(true);
        }
    }

    private void RetireCancellationSource(
        CancellationTokenSource cancellation,
        Task? cancellationCallbacks,
        Task operationCompletion,
        IDisposable lifetimeWork,
        string owner)
    {
        Task callbacks = cancellationCallbacks ??
            (cancellation.IsCancellationRequested
                ? CancellationTokenSourceRetirement.RequestCancellation(cancellation)
                : Task.CompletedTask);
        Task retirement;
        try
        {
            // The caller invokes this only after the operation task has
            // completed. Pass that task explicitly as an additional retirement
            // guard so disposal remains tied to both resolver and token work.
            retirement = CancellationTokenSourceRetirement.DisposeAfter(
                cancellation, callbacks, operationCompletion);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
            try { cancellation.Dispose(); }
            catch (Exception disposeException)
            {
                RecordCancellationRetirementFailure(owner, disposeException);
            }
            finally
            {
                try { lifetimeWork.Dispose(); }
                catch (Exception releaseException)
                {
                    RecordCancellationRetirementFailure(owner, releaseException);
                }
            }
            return;
        }

        _ = FinishCancellationRetirementAsync(callbacks, retirement, lifetimeWork, owner);
    }

    private async Task FinishCancellationRetirementAsync(
        Task callbacks,
        Task retirement,
        IDisposable lifetimeWork,
        string owner)
    {
        try
        {
            // DisposeAfter observes these inputs too, but awaiting the callback
            // task here preserves its exception for the session's diagnostics.
            await callbacks.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
        }

        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
        }
        finally
        {
            try { lifetimeWork.Dispose(); }
            catch (Exception exception)
            {
                RecordCancellationRetirementFailure(owner, exception);
            }
        }
    }

    private void RecordCancellationRetirementFailure(string owner, Exception exception)
    {
        Interlocked.Increment(ref _cancellationRetirementFailures);
        try
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] {owner} cancellation retirement failed: {exception.GetType().Name}");
        }
        catch
        {
        }
    }

    private bool TryGetSourceCacheMetadata(SourceKey key, out CachedSourceIdentity identity)
    {
        identity = default;
        if (key.Source.Length > 2048)
            return false;
        lock (_cacheGate)
        {
            if (IsDisposed || !_sourceCache.TryGetValue(key, out CachedSource? cached))
                return false;
            // Do not return or retain the asset/entry while waiting for byte
            // admission. The identity is scalar so eviction can proceed safely.
            identity = new CachedSourceIdentity(cached.Identity, cached.Asset.Bytes.Length);
            return true;
        }
    }

    private bool TryGetPrefetchedRasterIntrinsicMetadata(
        SourceKey sharedKey,
        SourceKey localKey,
        out RasterIntrinsicMetadata metadata)
    {
        metadata = default;
        lock (_cacheGate)
        {
            if (IsDisposed)
                return false;

            // Match the authoritative source lookup order. If a shared entry is
            // present without a hint, do not fall through to a potentially stale
            // document-local value for the same request.
            if (_sourceCache.TryGetValue(sharedKey, out CachedSource? shared))
            {
                if (shared.RasterIntrinsicMetadata is not { } sharedMetadata)
                    return false;
                metadata = sharedMetadata;
                return true;
            }
            if (_sourceCache.TryGetValue(localKey, out CachedSource? local))
            {
                if (local.RasterIntrinsicMetadata is not { } localMetadata)
                    return false;
                metadata = localMetadata;
                return true;
            }
            return false;
        }
    }

    private bool TryGetSourceCacheIdentity(
        SourceKey key,
        MarkdownImageAsset expectedAsset,
        out CachedSourceIdentity identity)
    {
        identity = default;
        if (key.Source.Length > 2048)
            return false;
        lock (_cacheGate)
        {
            if (IsDisposed || !_sourceCache.TryGetValue(key, out CachedSource? cached) ||
                !ReferenceEquals(cached.Asset, expectedAsset))
            {
                return false;
            }

            identity = new CachedSourceIdentity(cached.Identity, cached.Asset.Bytes.Length);
            return true;
        }
    }

    private bool TryPublishRasterIntrinsicMetadata(
        SourceKey key,
        CachedSourceIdentity expected,
        MarkdownImageAsset expectedAsset,
        RasterIntrinsicMetadata metadata)
    {
        lock (_cacheGate)
        {
            if (IsDisposed || !_sourceCache.TryGetValue(key, out CachedSource? cached) ||
                cached.Identity != expected.EntryIdentity ||
                cached.Asset.Bytes.Length != expected.ByteLength ||
                !ReferenceEquals(cached.Asset, expectedAsset))
            {
                return false;
            }

            if (cached.RasterIntrinsicMetadata is { } current)
                return current == metadata;
            if (_sourceCacheMetadataBytes > SourceCacheMetadataBudgetBytes -
                CachedSource.RasterIntrinsicMetadataWeightBytes)
            {
                return false;
            }

            cached.RasterIntrinsicMetadata = metadata;
            cached.MetadataWeight += CachedSource.RasterIntrinsicMetadataWeightBytes;
            _sourceCacheMetadataBytes += CachedSource.RasterIntrinsicMetadataWeightBytes;
            return true;
        }
    }

    private bool TryExposeSourceCacheEntry(
        SourceKey key,
        CachedSourceIdentity expected,
        out MarkdownImageAsset? asset)
    {
        asset = null;
        lock (_cacheGate)
        {
            if (IsDisposed || !_sourceCache.TryGetValue(key, out CachedSource? cached) ||
                cached.Identity != expected.EntryIdentity ||
                cached.Asset.Bytes.Length != expected.ByteLength)
            {
                return false;
            }

            _sourceRecency.Remove(cached.Node);
            _sourceRecency.AddFirst(cached.Node);
            Interlocked.Increment(ref _sourceCacheHits);
            asset = cached.Asset;
            return true;
        }
    }

    private async ValueTask<IMarkdownImageResolutionLease?> TryGetLeasedSourceAsync(
        SourceKey key,
        DocumentScope owner,
        bool background,
        CancellationToken cancellationToken)
    {
        // An entry can be evicted or replaced while admission is queued. Keep
        // only a length/identity snapshot across the await, then revalidate it
        // before exposing the array. A stale snapshot releases its grant and
        // retries once; a later miss simply takes the normal resolver path.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!TryGetSourceCacheMetadata(key, out CachedSourceIdentity identity))
                return null;
            if (background && identity.ByteLength > _sourceByteAdmission.BackgroundCapacity)
                throw new MarkdownImageSourceDeferredException();
            if (identity.ByteLength > _sourceByteAdmission.Capacity)
                return null;

            IDisposable? reservation = identity.ByteLength == 0
                ? null
                : await ReserveSourceByteLeaseAsync(
                        owner,
                        identity.ByteLength,
                        background,
                        cancellationToken,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            try
            {
                if (!TryExposeSourceCacheEntry(key, identity, out MarkdownImageAsset? asset) || asset is null)
                    continue;

                var lease = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(asset), reservation);
                reservation = null;
                return lease;
            }
            finally
            {
                reservation?.Dispose();
            }
        }

        return null;
    }

    private void Store(SourceKey sharedKey, SourceKey localKey, MarkdownImageResolution resolution)
    {
        MarkdownImageAsset? asset = resolution.Asset;
        if (asset is null || Options.SourceCacheBudgetBytes == 0 || sharedKey.Source.Length > 2048 ||
            asset.Bytes.Length > Options.SourceCacheBudgetBytes)
            return;

        SourceKey key = string.IsNullOrEmpty(asset.CacheKey) ? localKey : sharedKey;
        long metadataWeight = EstimateSourceMetadataWeight(key, asset);
        if (metadataWeight > SourceCacheMetadataBudgetBytes)
            return; // A cache miss never changes the authoritative resolution.

        lock (_cacheGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || key.DocumentScope?.IsDisposed == true)
                return;
            if (_sourceCache.TryGetValue(key, out CachedSource? old))
            {
                _sourceRecency.Remove(old.Node);
                _sourceCache.Remove(key);
                _sourceCacheBytes -= old.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= old.MetadataWeight;
            }
            var entry = new CachedSource(
                key, asset, metadataWeight, unchecked(++_sourceCacheEntryIdentity));
            entry.Node = _sourceRecency.AddFirst(entry);
            _sourceCache.Add(key, entry);
            _sourceCacheBytes += asset.Bytes.Length;
            _sourceCacheMetadataBytes += metadataWeight;
            if (key.DocumentScope is { } scope)
            {
                if (!_documentSourceKeys.TryGetValue(scope, out HashSet<SourceKey>? keys))
                    _documentSourceKeys.Add(scope, keys = new HashSet<SourceKey>(SourceKeyComparer.Instance));
                keys.Add(key);
            }
            while ((_sourceCacheBytes > Options.SourceCacheBudgetBytes ||
                    _sourceCacheMetadataBytes > SourceCacheMetadataBudgetBytes) &&
                   _sourceRecency.Last is { } last)
            {
                _sourceRecency.RemoveLast();
                _sourceCache.Remove(last.Value.Key);
                _sourceCacheBytes -= last.Value.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= last.Value.MetadataWeight;
                if (last.Value.Key.DocumentScope is { } evictedScope &&
                    _documentSourceKeys.TryGetValue(evictedScope, out HashSet<SourceKey>? evictedKeys))
                {
                    evictedKeys.Remove(last.Value.Key);
                    if (evictedKeys.Count == 0)
                        _documentSourceKeys.Remove(evictedScope);
                }
                Interlocked.Increment(ref _sourceCacheEvictions);
            }
        }
    }

    private static long EstimateSourceMetadataWeight(SourceKey key, MarkdownImageAsset asset)
    {
        // Includes entry/node/hash-table amortization, local-owner key indexing,
        // asset/array headers and context carriers. Count borrowed strings too:
        // overestimating shared context is safer than uncharged giant cache keys.
        static long TextWeight(string? text) => text is null ? 0 : 32L + 2L * text.Length;
        MarkdownImageResolveContext context = key.Context;
        MarkdownDocumentSource? document = context.DocumentSource;
        return 512L + TextWeight(key.Source) + TextWeight(asset.CacheKey) +
            TextWeight(asset.ContentType) + TextWeight(asset.ResolvedUri?.OriginalString) +
            TextWeight(context.BaseUri?.OriginalString) + TextWeight(context.DocumentPath) +
            TextWeight(document?.DocumentId) + TextWeight(document?.Owner) +
            TextWeight(document?.Repository) + TextWeight(document?.Ref) + TextWeight(document?.Path);
    }

    private void RemoveDocumentEntries(DocumentScope scope)
    {
        lock (_cacheGate)
        {
            if (!_documentSourceKeys.Remove(scope, out HashSet<SourceKey>? keys))
                return;
            foreach (SourceKey key in keys)
            {
                if (!_sourceCache.Remove(key, out CachedSource? cached))
                    continue;
                _sourceRecency.Remove(cached.Node);
                _sourceCacheBytes -= cached.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= cached.MetadataWeight;
            }
            if (_sourceCache.Count == 0)
                ResetSourceCacheNoLock();
        }
    }

    private async Task<IMarkdownImageResolutionLease> FetchAsync(
        DocumentScope owner,
        SourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        Action onResolveStarted,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , MarkdownImageActivationCorrelation? activationTrace = null
#endif
        )
    {
        RegisterWork();
        Interlocked.Increment(ref _pendingImageFetches);
        IDisposable? backgroundLease = null;
        bool fetchAcquired = false;
        bool resolverStarted = false;
        try
        {
            // Source-cache hits are admitted before their byte[] is exposed and
            // before any fetch slot is held. A waiter for source capacity must
            // never occupy a network slot.
            IMarkdownImageResolutionLease? cachedLease = await TryGetLeasedSourceAsync(
                    key, owner, background, cancellationToken)
                .ConfigureAwait(false);
            if (cachedLease is not null)
                return cachedLease;

            if (background)
            {
                bool remote = IsRemoteSource(key.Source, context);
                while (true)
                {
                    // Paused remote work must not hold a speculative slot that
                    // another document could use for local resources.
                    while (remote && ShouldPauseRemotePrefetch())
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                    backgroundLease = await _backgroundAdmission.EnterAsync(
                        owner, cancellationToken).ConfigureAwait(false);
                    if (!remote || !ShouldPauseRemotePrefetch())
                        break;
                    backgroundLease.Dispose();
                    backgroundLease = null;
                }
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            activationTrace?.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageFetchSlotWaitStarted);
#endif
            await _fetchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            fetchAcquired = true;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            activationTrace?.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageFetchSlotAcquired);
#endif
            onResolveStarted();
            long started = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _pendingImageFetches);
            Interlocked.Increment(ref _activeImageFetches);
            resolverStarted = true;
            Interlocked.Increment(ref _imageFetches);
            try
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                activationTrace?.RecordStage(
                    MarkdownPerformanceEventSource.ImageActivationStageHostResolverEntered);
#endif
                IMarkdownImageResolutionLease resolutionLease;
                using var sourceAdmission = new ResolverSourceByteAdmission(
                    this, _sourceByteAdmission, owner, background, cancellationToken);
                if (resolver is IMarkdownLeasedImageResolver leased)
                {
                    resolutionLease = await leased.ResolveWithSourceLeaseAsync(
                            key.Source, context, sourceAdmission, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Compatibility resolvers have no transferable source
                    // lifetime. Their results retain the historical behavior
                    // and are outside the strict in-flight byte guarantee.
                    MarkdownImageResolution resolution = resolver is IMarkdownImageSourceByteAdmittedResolver admitted
                        ? await admitted.ResolveWithSourceByteAdmissionAsync(
                            key.Source, context, sourceAdmission, cancellationToken).ConfigureAwait(false)
                        : await resolver.ResolveAsync(
                            key.Source, context, cancellationToken).ConfigureAwait(false);
                    resolutionLease = MarkdownImageResolutionLease.CreateUnreserved(resolution);
                }

                ArgumentNullException.ThrowIfNull(resolutionLease);
                try
                {
                    MarkdownImageResolution result = resolutionLease.Resolution;
                    if (!string.IsNullOrEmpty(result.Asset?.CacheKey))
                        Store(key, key, result);
                    return resolutionLease;
                }
                catch
                {
                    DisposeResolutionLeaseSafely(resolutionLease);
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _imageFetchCancellations);
                throw;
            }
            catch (MarkdownImageSourceDeferredException) when (background)
            {
                // A speculative source that needs a larger reservation is not
                // a failed image. A later visible request retries it.
                throw;
            }
            catch
            {
                Interlocked.Increment(ref _imageFetchFailures);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeImageFetches);
                long elapsedTicks = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _imageFetchMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                MarkdownPerformanceEventSource.Log.ResourceWork(1, elapsedTicks, 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !resolverStarted)
        {
            Interlocked.Increment(ref _imageFetchCancellations);
            throw;
        }
        finally
        {
            try
            {
                if (!resolverStarted)
                    Interlocked.Decrement(ref _pendingImageFetches);
                if (fetchAcquired) _fetchSlots.Release();
            }
            finally
            {
                try { backgroundLease?.Dispose(); }
                finally { RetireWork(); }
            }
        }
    }

    private async Task<IMarkdownImageResolutionLease> ResolveSharedAsync(
        DocumentScope owner,
        SourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , MarkdownImageActivationCorrelation? activationTrace = null
#endif
        )
    {
        SharedFetch flight;
        SharedFetch? superseded = null;
        bool startFlight = false;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        bool joinedExisting = false;
#endif
        lock (_flightGate)
        {
            if (IsDisposed)
                throw new OperationCanceledException(_lifetimeToken);
            if (!_sharedFlights.TryGetValue(key, out flight!) ||
                (!background && flight.Background))
            {
                RasterWorkLease? flightLifetime = TryBeginWorkLease();
                if (flightLifetime is null)
                    throw new OperationCanceledException(_lifetimeToken);
                CancellationTokenSource? source = null;
                try
                {
                    if (_sharedFlights.TryGetValue(key, out SharedFetch? previous))
                    {
                        superseded = previous;
                        _sharedFlights.Remove(key);
                    }

                    source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                    flight = new SharedFetch(source, background, this, flightLifetime);
                    _ = flight.Task.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    _sharedFlights.Add(key, flight);
                    source = null;
                    flightLifetime = null;
                    startFlight = true;
                }
                catch
                {
                    source?.Dispose();
                    flightLifetime?.Dispose();
                    throw;
                }
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            else
            {
                joinedExisting = true;
            }
#endif

            flight.WaiterCount++;
        }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        if (joinedExisting)
        {
            activationTrace?.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageSharedFlightJoined);
        }
        if (superseded is not null && !background)
        {
            activationTrace?.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageBackgroundFlightSuperseded);
        }
#endif

        // Publish the shared task while holding the gate, but do not run the
        // async method's synchronous prefix there. SemaphoreSlim.WaitAsync can
        // complete inline, and a resolver is allowed to do synchronous work
        // before returning its ValueTask. Running that prefix outside the gate
        // keeps unrelated source flights admissible.
        if (startFlight)
        {
            SharedFetch startedFlight = flight;
            startedFlight.Start(
                () => FetchAsync(
                    owner, key, resolver, context, background, startedFlight.MarkResolutionStarted,
                    startedFlight.Cancellation.Token
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , activationTrace
#endif
                    ),
                (result, operationCompletion) =>
                {
                    try
                    {
                        bool releaseRoot;
                        lock (_flightGate)
                        {
                            if (_sharedFlights.TryGetValue(key, out SharedFetch? current) &&
                                ReferenceEquals(current, startedFlight))
                                _sharedFlights.Remove(key);
                            bool published = startedFlight.SetResult(result);
                            releaseRoot = !published || startedFlight.WaiterCount == 0;
                        }
                        if (releaseRoot)
                            startedFlight.ReleaseRootSafely();
                    }
                    finally
                    {
                        startedFlight.RetireCancellation(operationCompletion);
                    }
                },
                (exception, operationCompletion) =>
                {
                    try
                    {
                        bool releaseRoot;
                        lock (_flightGate)
                        {
                            if (_sharedFlights.TryGetValue(key, out SharedFetch? current) &&
                                ReferenceEquals(current, startedFlight))
                                _sharedFlights.Remove(key);
                            startedFlight.SetException(exception);
                            releaseRoot = startedFlight.WaiterCount == 0;
                        }
                        if (releaseRoot)
                            startedFlight.ReleaseRootSafely();
                    }
                    finally
                    {
                        startedFlight.RetireCancellation(operationCompletion);
                    }
                });
        }

        if (superseded is not null)
        {
            try { superseded.RequestCancellation(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return flight.RetainResult();
        }
        finally
        {
            bool cancel = false;
            bool releaseRoot = false;
            lock (_flightGate)
            {
                if (--flight.WaiterCount == 0)
                {
                    if (!flight.Task.IsCompleted)
                    {
                        if (_sharedFlights.TryGetValue(key, out SharedFetch? current) &&
                            ReferenceEquals(current, flight))
                            _sharedFlights.Remove(key);
                        cancel = true;
                    }
                    else
                    {
                        releaseRoot = true;
                    }
                }
            }
            if (cancel)
            {
                try { flight.RequestCancellation(); }
                catch (ObjectDisposedException) { }
            }
            if (releaseRoot)
                flight.ReleaseRootSafely();
        }
    }

    private sealed class SharedFetch(
        CancellationTokenSource cancellation,
        bool background,
        MarkdownPerformanceSession session,
        IDisposable lifetimeWork)
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ResolutionLeaseSlot _result = new();
        private readonly CancellationRetirementHandshake _cancellationRetirement = new(cancellation);
        private int _resolutionStarted;
        private IDisposable? _lifetimeWork = lifetimeWork;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        private MarkdownPerformanceSession Session { get; } = session;
        internal bool Background { get; } = background;
        internal Task<bool> Task => _completion.Task;
        internal int WaiterCount { get; set; }
        internal bool HasStartedResolution => Volatile.Read(ref _resolutionStarted) != 0;
        internal void MarkResolutionStarted() => Volatile.Write(ref _resolutionStarted, 1);

        internal void Start(
            Func<Task<IMarkdownImageResolutionLease>> operation,
            Action<IMarkdownImageResolutionLease, Task> onSuccess,
            Action<Exception, Task> onFailure)
        {
            Task completion = CompleteAsync(operation, onSuccess, onFailure);
            MarkdownRenderer.TaskFaultObservation.ObserveFault(completion);
        }

        private static async Task CompleteAsync(
            Func<Task<IMarkdownImageResolutionLease>> operation,
            Action<IMarkdownImageResolutionLease, Task> onSuccess,
            Action<Exception, Task> onFailure)
        {
            Task<IMarkdownImageResolutionLease>? operationCompletion = null;
            try
            {
                operationCompletion = operation();
                onSuccess(
                    await operationCompletion.ConfigureAwait(false),
                    operationCompletion);
            }
            catch (Exception exception)
            {
                onFailure(exception, operationCompletion ?? System.Threading.Tasks.Task.CompletedTask);
            }
        }

        internal void RequestCancellation()
            => _cancellationRetirement.RequestCancellation();

        internal void RetireCancellation(Task operationCompletion)
        {
            Task? callbacks = _cancellationRetirement.BeginRetirement();
            IDisposable? lifetime = Interlocked.Exchange(ref _lifetimeWork, null);
            if (lifetime is null)
                return;

            Session.RetireCancellationSource(
                Cancellation,
                callbacks,
                operationCompletion,
                lifetime,
                "shared image fetch");
        }

        internal bool SetResult(IMarkdownImageResolutionLease result)
        {
            _result.SetRoot(result);
            return _completion.TrySetResult(true);
        }

        internal IMarkdownImageResolutionLease RetainResult() =>
            _result.TryRetain(out IMarkdownImageResolutionLease? retained)
                ? retained
                : throw new InvalidOperationException("The shared image result was released before its waiter claimed it.");

        internal void ReleaseRootSafely() => _result.ReleaseRootSafely();

        internal void SetException(Exception exception)
        {
            if (exception is OperationCanceledException canceled)
                _completion.TrySetCanceled(canceled.CancellationToken);
            else
                _completion.TrySetException(exception);
        }

    }

    /// <summary>
    /// Serializes a request's cancellation decision with cancellation-source
    /// retirement. Once retirement starts, no late caller can schedule token
    /// callbacks after the source's callback task has been captured.
    /// </summary>
    internal sealed class CancellationRetirementHandshake(CancellationTokenSource source)
    {
        private readonly object _gate = new();
        private Task? _callbacks;
        private bool _retirementStarted;

        internal Task? RequestCancellation()
        {
            lock (_gate)
            {
                if (_retirementStarted)
                    return _callbacks;

                return _callbacks ??= CancellationTokenSourceRetirement.RequestCancellation(source);
            }
        }

        internal Task? BeginRetirement()
        {
            lock (_gate)
            {
                _retirementStarted = true;
                return _callbacks;
            }
        }
    }

    /// <summary>
    /// Holds only the unclaimed root handle. Its TaskCompletionSource publishes
    /// a bool, never the raw image result, so a completed fan-out task cannot
    /// keep a released byte[] alive.
    /// </summary>
    private sealed class ResolutionLeaseSlot
    {
        private readonly object _gate = new();
        private IMarkdownImageResolutionLease? _root;

        internal void SetRoot(IMarkdownImageResolutionLease root)
        {
            ArgumentNullException.ThrowIfNull(root);
            lock (_gate)
            {
                if (_root is not null)
                    throw new InvalidOperationException("An image result lease was already published.");
                _root = root;
            }
        }

        internal bool TryRetain([NotNullWhen(true)] out IMarkdownImageResolutionLease? retained)
        {
            lock (_gate)
            {
                if (_root is null)
                {
                    retained = null;
                    return false;
                }

                retained = _root.Retain();
                return true;
            }
        }

        internal void ReleaseRootSafely()
        {
            IMarkdownImageResolutionLease? root;
            lock (_gate)
            {
                root = _root;
                _root = null;
            }

            if (root is not null)
                DisposeResolutionLeaseSafely(root);
        }
    }

    private static void DisposeResolutionLeaseSafely(IMarkdownImageResolutionLease lease)
    {
        try
        {
            lease.Dispose();
        }
        catch (Exception exception)
        {
            // Never run provider cleanup under a cache/flight lock or let it
            // replace a resolver failure. Diagnostics omit source identity.
            try
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] image result lease cleanup failed: {exception.GetType().Name}");
            }
            catch
            {
            }
        }
    }

    private static bool ShouldPauseRemotePrefetch()
    {
        try
        {
            if (PowerManager.EnergySaverStatus == EnergySaverStatus.On)
                return true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // Unpackaged and headless hosts do not always expose power state.
        }

        try
        {
            ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost is not null &&
                (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable ||
                 cost.Roaming || cost.OverDataLimit);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            return false;
        }
    }

    private static bool IsRemoteSource(string source, MarkdownImageResolveContext context)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (context.BaseUri is null || !Uri.TryCreate(context.BaseUri, source, out uri)))
            return false;
        return uri.Scheme is "https" or "http";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task drain;
        TaskCompletionSource<bool> completed;
        EventHandler? disposedHandlers;
        lock (_workGate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            disposedHandlers = _disposedHandlers;
            _disposedHandlers = null;
            _workDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeWork == 0)
                _workDrained.SetResult(true);
            drain = _workDrained.Task;
            completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompleted = completed;
        }

        _layoutWorkScheduler.Dispose();
        _imageActivationScheduler.Dispose();

        ParseStageDiagnosticRecorder = null;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        IsLayoutDiagnosticCaptureEnabled = false;
#endif
        ReleaseAllProgressiveScenePlans();
        CompletedSceneCache.Dispose();
        ReleaseAllDocumentScopes();
        NotifyDisposedHandlers(disposedHandlers);

        Task cancellation = _lifetime.CancelAsync();
        if (_memoryPressureSubscribed)
        {
            try { Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased; }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or
                PlatformNotSupportedException or TypeInitializationException)
            {
            }
        }
        Trim();
        _sourceResolverIdentities.Clear();
        _ = FinishShutdownAsync(cancellation, drain, completed);
    }

    private void NotifyDisposedHandlers(EventHandler? handlers)
    {
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Disposal observer failed: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? completion;
        lock (_workGate)
            completion = _shutdownCompleted?.Task;
        if (completion is not null)
            await completion.ConfigureAwait(false);
    }

    private async Task FinishShutdownAsync(
        Task cancellation,
        Task drain,
        TaskCompletionSource<bool> completed)
    {
        List<Exception>? failures = null;
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            // Parse/plan creation is included in the work drain above. Drain
            // after it so plans retired by the last in-flight parse are also
            // part of session shutdown, not only plans present at Dispose().
            await _scenePlanRetirements.DrainAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await _layoutWorkScheduler.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await _imageActivationScheduler.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            _layoutWorkScheduler.ReleaseResources();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }
        finally
        {
            _fetchSlots.Dispose();
            _lifetime.Dispose();
        }

        if (failures is null)
            completed.TrySetResult(true);
        else if (failures.Count == 1)
            completed.TrySetException(failures[0]);
        else
            completed.TrySetException(new AggregateException(failures));
    }

    internal sealed class DocumentScope : IMarkdownPerformanceDocumentScope
    {
        private readonly MarkdownPerformanceSession _session;
        private readonly IMarkdownImageResolver _resolver;
        private readonly object _resolverCacheIdentity;
        private readonly MarkdownImageResolveContext _context;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly object _inFlightGate = new();
        private readonly Dictionary<string, InflightRequest> _inFlight =
            new(StringComparer.Ordinal);
        private int _disposed;

        internal DocumentScope(
            MarkdownPerformanceSession session,
            IMarkdownImageResolver resolver,
            MarkdownImageResolveContext context)
        {
            _session = session;
            _resolver = resolver;
            // Cache keys preserve resolver reference identity without owning
            // arbitrary host/plugin state after its last document is closed.
            _resolverCacheIdentity = session._sourceResolverIdentities.GetValue(
                resolver, static _ => new object());
            _context = context;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(session._lifetimeToken);
            _token = _lifetime.Token;
        }

        internal bool Matches(
            IMarkdownPerformanceSessionInternal session,
            IMarkdownImageResolver resolver,
            MarkdownImageResolveContext context) =>
            ReferenceEquals(_session, session) &&
            ReferenceEquals(_resolver, resolver) &&
            Equals(_context, context) &&
            Volatile.Read(ref _disposed) == 0;

        internal CancellationToken CancellationToken => _token;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        bool IMarkdownPerformanceDocumentScope.IsDisposed => IsDisposed;
        CancellationToken IMarkdownPerformanceDocumentScope.CancellationToken => CancellationToken;
        bool IMarkdownPerformanceDocumentScope.Matches(
            IMarkdownPerformanceSessionInternal session,
            IMarkdownImageResolver resolver,
            MarkdownImageResolveContext context) => Matches(session, resolver, context);
        bool IMarkdownPerformanceDocumentScope.TryGetPrefetchedRasterIntrinsicMetadata(
            string source,
            out RasterIntrinsicMetadata metadata)
        {
            metadata = default;
            if (IsDisposed || !_session.Options.PrefetchDocumentImages ||
                !_session.Options.UseDisplaySizedRasterDecode ||
                source is null || source.Length > 2048)
            {
                return false;
            }

            SourceKey sharedKey = new(_resolverCacheIdentity, _context, source, null);
            SourceKey localKey = sharedKey with { DocumentScope = this };
            return _session.TryGetPrefetchedRasterIntrinsicMetadata(
                sharedKey, localKey, out metadata);
        }
        Task IMarkdownPerformanceDocumentScope.PrefetchAsync(
            IReadOnlyList<string> sources,
            CancellationToken cancellationToken) => PrefetchAsync(sources, cancellationToken);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        async ValueTask<MarkdownImageResolution> IMarkdownPerformanceDocumentScope.ResolveWithActivationTraceAsync(
            string source,
            MarkdownImageResolveContext context,
            MarkdownImageActivationCorrelation correlation,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException(_token);
            if (!Equals(context, _context))
                throw new InvalidOperationException("The image resolution context changed during document preparation.");

            correlation.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageDocumentScopeEntered);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            return await ResolveCoreAsync(
                    source, background: false, linked.Token, correlation)
                .ConfigureAwait(false);
        }

        async ValueTask<IMarkdownImageResolutionLease>
            IMarkdownPerformanceDocumentScope.ResolveWithSourceLeaseAndActivationTraceAsync(
                string source,
                MarkdownImageResolveContext context,
                MarkdownImageActivationCorrelation correlation,
                CancellationToken cancellationToken)
        {
            ValidateResolutionRequest(context);
            correlation.RecordStage(
                MarkdownPerformanceEventSource.ImageActivationStageDocumentScopeEntered);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            return await ResolveCoreWithSourceLeaseAsync(
                    source, background: false, linked.Token, correlation)
                .ConfigureAwait(false);
        }
#endif

        async ValueTask<IMarkdownImageResolutionLease>
            IMarkdownPerformanceDocumentScope.ResolveWithSourceLeaseAsync(
                string source,
                MarkdownImageResolveContext context,
                CancellationToken cancellationToken)
        {
            ValidateResolutionRequest(context);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            return await ResolveCoreWithSourceLeaseAsync(
                    source, background: false, linked.Token)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            ValidateResolutionRequest(context);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            return await ResolveCoreAsync(source, background: false, linked.Token)
                .ConfigureAwait(false);
        }

        private void ValidateResolutionRequest(MarkdownImageResolveContext context)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException(_token);
            if (!Equals(context, _context))
                throw new InvalidOperationException("The image resolution context changed during document preparation.");
        }

        internal async Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken)
        {
            if (!_session.Options.PrefetchDocumentImages || sources.Count == 0)
                return;
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            await Parallel.ForEachAsync(
                sources,
                new ParallelOptions
                {
                    CancellationToken = linked.Token,
                    MaxDegreeOfParallelism = _session.Options.MaxConcurrentImageFetches -
                        _session.Options.ReservedVisibleImageFetches,
                },
                async (source, token) =>
                {
                    try
                    {
                        if (!_session.Options.UseDisplaySizedRasterDecode)
                        {
                            await ResolveCoreAsync(source, background: true, token).ConfigureAwait(false);
                            return;
                        }

                        if (((IMarkdownPerformanceDocumentScope)this)
                            .TryGetPrefetchedRasterIntrinsicMetadata(source, out _))
                        {
                            return;
                        }

                        IMarkdownImageResolutionLease? sourceLease = null;
                        try
                        {
                            sourceLease = await ResolveCoreWithSourceLeaseAsync(
                                    source, background: true, token)
                                .ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            MarkdownImageAsset? asset = sourceLease.Resolution.Asset;
                            if (asset is null ||
                                !TryCapturePrefetchedRasterTarget(
                                    source, asset, out SourceKey storedKey, out CachedSourceIdentity identity))
                            {
                                return;
                            }

                            // Header parsing is bounded but scans untrusted input.
                            // Keep it on the opted-in prefetch worker and inside
                            // the existing CPU/source admission lifetimes.
                            using IDisposable cpuAdmission = await _session.EnterCpuPreparationAsync(
                                    this, exclusive: false, token)
                                .ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (!RasterIntrinsicMetadata.TryRead(
                                    asset.Bytes, out RasterIntrinsicMetadata metadata, token))
                            {
                                return;
                            }
                            token.ThrowIfCancellationRequested();
                            TryPublishPrefetchedRasterIntrinsicMetadata(
                                storedKey, identity, asset, metadata);
                        }
                        finally
                        {
                            if (sourceLease is not null)
                                DisposeResolutionLeaseSafely(sourceLease);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        // Visible loads remain authoritative and can retry failures.
                    }
                }).ConfigureAwait(false);
        }

        private bool TryCapturePrefetchedRasterTarget(
            string source,
            MarkdownImageAsset asset,
            out SourceKey storedKey,
            out CachedSourceIdentity identity)
        {
            identity = default;
            storedKey = default;
            if (IsDisposed || _session.IsDisposed)
                return false;

            SourceKey sharedKey = new(_resolverCacheIdentity, _context, source, null);
            SourceKey localKey = sharedKey with { DocumentScope = this };
            // Select the same shared-vs-document key used by Store before
            // capturing its identity; a later replacement must not accept
            // metadata parsed from this lease.
            storedKey = string.IsNullOrEmpty(asset.CacheKey) ? localKey : sharedKey;
            return _session.TryGetSourceCacheIdentity(storedKey, asset, out identity);
        }

        private void TryPublishPrefetchedRasterIntrinsicMetadata(
            SourceKey storedKey,
            CachedSourceIdentity identity,
            MarkdownImageAsset asset,
            RasterIntrinsicMetadata metadata)
        {
            if (IsDisposed || _session.IsDisposed)
                return;
            _session.TryPublishRasterIntrinsicMetadata(storedKey, identity, asset, metadata);
        }

        private async Task<MarkdownImageResolution> ResolveCoreAsync(
            string source,
            bool background,
            CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , MarkdownImageActivationCorrelation? activationTrace = null
#endif
            )
        {
            IMarkdownImageResolutionLease lease = await ResolveCoreWithSourceLeaseAsync(
                    source, background, cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , activationTrace
#endif
                    )
                .ConfigureAwait(false);
            try
            {
                return lease.Resolution;
            }
            finally
            {
                // Compatibility callers receive the historical raw result.
                // Their byte lifetime is outside the strict retained-source
                // guarantee, so release any transferred provider reservation.
                DisposeResolutionLeaseSafely(lease);
            }
        }

        private async Task<IMarkdownImageResolutionLease> ResolveCoreWithSourceLeaseAsync(
            string source,
            bool background,
            CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , MarkdownImageActivationCorrelation? activationTrace = null
#endif
            )
        {
            SourceKey key = new(_resolverCacheIdentity, _context, source, null);
            SourceKey localKey = key with { DocumentScope = this };
            IMarkdownImageResolutionLease? cachedLease = await _session.TryGetLeasedSourceAsync(
                    key, this, background, cancellationToken)
                .ConfigureAwait(false);
            cachedLease ??= await _session.TryGetLeasedSourceAsync(
                    localKey, this, background, cancellationToken)
                .ConfigureAwait(false);
            if (cachedLease is not null)
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            {
                activationTrace?.RecordStage(
                    MarkdownPerformanceEventSource.ImageActivationStageDocumentSourceCacheHit);
                return cachedLease;
            }
#else
                return cachedLease;
#endif

            InflightRequest request;
            InflightRequest? abandoned = null;
            bool startRequest = false;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            bool joinedExisting = false;
#endif
            lock (_inFlightGate)
            {
                if (_disposed != 0)
                    throw new OperationCanceledException(_token);
                if (_inFlight.TryGetValue(source, out InflightRequest? existing) &&
                    !background && existing.Background)
                {
                    // A queued or paused prefetch must never hold up a visible
                    // image. Its cancellation retires the speculative slot.
                    abandoned = existing;
                    _inFlight.Remove(source);
                }

                if (!_inFlight.TryGetValue(source, out request!))
                {
                    RasterWorkLease? requestLifetime = _session.TryBeginWorkLease();
                    if (requestLifetime is null)
                        throw new OperationCanceledException(_session._lifetimeToken);
                    CancellationTokenSource? linked = null;
                    try
                    {
                        linked = CancellationTokenSource.CreateLinkedTokenSource(_token);
                        request = new InflightRequest(
                            linked, background, _session, requestLifetime)
                        { WaiterCount = 1 };
                        _ = request.Task.ContinueWith(
                            static completed => _ = completed.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        _inFlight.Add(source, request);
                        linked = null;
                        requestLifetime = null;
                        startRequest = true;
                    }
                    catch
                    {
                        linked?.Dispose();
                        requestLifetime?.Dispose();
                        throw;
                    }
                }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                else
                {
                    joinedExisting = true;
                    request.WaiterCount++;
                }
#else
                else
                {
                    request.WaiterCount++;
                }
#endif
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (joinedExisting)
            {
                activationTrace?.RecordStage(
                    MarkdownPerformanceEventSource.ImageActivationStageDocumentFlightJoined);
            }
#endif
            if (abandoned is not null)
            {
                try { abandoned.RequestCancellation(); }
                catch (ObjectDisposedException) { }
            }

            // Starting ResolveSharedAsync inside this gate would run through
            // its first incomplete await. That path can acquire a fetch slot
            // and call a host resolver synchronously, blocking every other
            // source in this document while the host is doing unrelated work.
            if (startRequest)
            {
                InflightRequest startedRequest = request;
                startedRequest.Start(
                    () => _session.ResolveSharedAsync(
                        this, key, _resolver, _context, background,
                        startedRequest.Cancellation.Token
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        , activationTrace
#endif
                        ),
                    (result, operationCompletion) =>
                    {
                        bool ownsRoot = false;
                        try
                        {
                            MarkdownImageResolution resolution = result.Resolution;
                            if (string.IsNullOrEmpty(resolution.Asset?.CacheKey))
                                _session.Store(key, localKey, resolution);

                            bool releaseRoot;
                            lock (_inFlightGate)
                            {
                                if (_inFlight.TryGetValue(source, out InflightRequest? current) &&
                                    ReferenceEquals(current, startedRequest))
                                    _inFlight.Remove(source);
                                bool published = startedRequest.SetResult(result);
                                ownsRoot = true;
                                releaseRoot = !published || startedRequest.WaiterCount == 0;
                            }
                            if (releaseRoot)
                                startedRequest.ReleaseRootSafely();
                        }
                        catch
                        {
                            if (!ownsRoot)
                                DisposeResolutionLeaseSafely(result);
                            throw;
                        }
                        finally
                        {
                            startedRequest.RetireCancellation(operationCompletion);
                        }
                    },
                    (exception, operationCompletion) =>
                    {
                        try
                        {
                            lock (_inFlightGate)
                            {
                                if (_inFlight.TryGetValue(source, out InflightRequest? current) &&
                                    ReferenceEquals(current, startedRequest))
                                    _inFlight.Remove(source);
                                startedRequest.SetException(exception);
                            }
                        }
                        finally
                        {
                            startedRequest.RetireCancellation(operationCompletion);
                        }
                    });
            }
            try
            {
                await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return request.RetainResult();
            }
            finally
            {
                bool cancel = false;
                bool releaseRoot = false;
                lock (_inFlightGate)
                {
                    if (--request.WaiterCount == 0)
                    {
                        if (!request.Task.IsCompleted)
                        {
                            if (_inFlight.TryGetValue(source, out InflightRequest? current) &&
                                ReferenceEquals(current, request))
                                _inFlight.Remove(source);
                            cancel = true;
                        }
                        else
                        {
                            releaseRoot = true;
                        }
                    }
                }
                if (cancel)
                {
                    try { request.RequestCancellation(); }
                    catch (ObjectDisposedException) { }
                }
                if (releaseRoot)
                    request.ReleaseRootSafely();
            }
        }

        private sealed class InflightRequest(
            CancellationTokenSource cancellation,
            bool background,
            MarkdownPerformanceSession session,
            IDisposable lifetimeWork)
        {
            private readonly TaskCompletionSource<bool> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ResolutionLeaseSlot _result = new();
            private readonly CancellationRetirementHandshake _cancellationRetirement = new(cancellation);
            private IDisposable? _lifetimeWork = lifetimeWork;
            internal CancellationTokenSource Cancellation { get; } = cancellation;
            internal bool Background { get; } = background;
            internal Task<bool> Task => _completion.Task;
            internal int WaiterCount { get; set; }

            internal void Start(
                Func<Task<IMarkdownImageResolutionLease>> operation,
                Action<IMarkdownImageResolutionLease, Task> onSuccess,
                Action<Exception, Task> onFailure)
            {
                Task completion = CompleteAsync(operation, onSuccess, onFailure);
                MarkdownRenderer.TaskFaultObservation.ObserveFault(completion);
            }

            private static async Task CompleteAsync(
                Func<Task<IMarkdownImageResolutionLease>> operation,
                Action<IMarkdownImageResolutionLease, Task> onSuccess,
                Action<Exception, Task> onFailure)
            {
                Task<IMarkdownImageResolutionLease>? operationCompletion = null;
                try
                {
                    operationCompletion = operation();
                    onSuccess(
                        await operationCompletion.ConfigureAwait(false),
                        operationCompletion);
                }
                catch (Exception exception)
                {
                    onFailure(exception, operationCompletion ?? System.Threading.Tasks.Task.CompletedTask);
                }
            }

            internal void RequestCancellation()
                => _cancellationRetirement.RequestCancellation();

            internal void RetireCancellation(Task operationCompletion)
            {
                Task? callbacks = _cancellationRetirement.BeginRetirement();
                IDisposable? lifetime = Interlocked.Exchange(ref _lifetimeWork, null);
                if (lifetime is null)
                    return;

                session.RetireCancellationSource(
                    Cancellation,
                    callbacks,
                    operationCompletion,
                    lifetime,
                    "document image request");
            }

            internal bool SetResult(IMarkdownImageResolutionLease result)
            {
                _result.SetRoot(result);
                return _completion.TrySetResult(true);
            }

            internal IMarkdownImageResolutionLease RetainResult() =>
                _result.TryRetain(out IMarkdownImageResolutionLease? retained)
                    ? retained
                    : throw new InvalidOperationException("The document image result was released before its waiter claimed it.");

            internal void ReleaseRootSafely() => _result.ReleaseRootSafely();

            internal void SetException(Exception exception)
            {
                if (exception is OperationCanceledException canceled)
                    _completion.TrySetCanceled(canceled.CancellationToken);
                else
                    _completion.TrySetException(exception);
            }

        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Task[] admitted;
            lock (_inFlightGate)
            {
                admitted = new Task[_inFlight.Count];
                int index = 0;
                foreach (InflightRequest request in _inFlight.Values)
                    admitted[index++] = request.Task;
            }
            _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
                _lifetime, Task.WhenAll(admitted));
            _session.RemoveDocumentEntries(this);
        }
    }

    private sealed class PerformanceDocumentState(
        string source,
        int registryRevision,
        DocumentScope scope)
    {
        internal string Source { get; } = source;
        internal int RegistryRevision { get; } = registryRevision;
        internal DocumentScope Scope { get; } = scope;
        internal bool PrefetchStarted { get; set; }
    }

    private readonly record struct SourceKey(
        object ResolverIdentity,
        MarkdownImageResolveContext Context,
        string Source,
        DocumentScope? DocumentScope);

    private sealed class ResolverSourceByteAdmission :
        IMarkdownImageSourceByteAdmission,
        IMarkdownImageSourceWorkTracker,
        IDisposable
    {
        private readonly object _workGate = new();
        private readonly MarkdownPerformanceSession _session;
        private readonly SourceByteAdmission _admission;
        private readonly DocumentScope _owner;
        private readonly bool _background;
        private readonly CancellationToken _requestCancellationToken;
        private int _trackedWorkCount;
        private bool _closed;
        private bool _sessionLifetimeReleased;

        internal ResolverSourceByteAdmission(
            MarkdownPerformanceSession session,
            SourceByteAdmission admission,
            DocumentScope owner,
            bool background,
            CancellationToken requestCancellationToken)
        {
            _session = session;
            _admission = admission;
            _owner = owner;
            _background = background;
            _requestCancellationToken = requestCancellationToken;

            // Reserve one shutdown-drain unit before exposing this scope to the
            // host. Child registration then only adjusts this already-owned
            // lifetime and never calls RegisterWork after session disposal.
            session.RegisterWork();
        }

        public bool IsSpeculative => _background;

        public long MaximumReservationBytes => _background
            ? _admission.BackgroundCapacity
            : _admission.Capacity;

        public async ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            if (_background && bytes > _admission.BackgroundCapacity)
                throw new MarkdownImageSourceDeferredException();
            return await _session.ReserveSourceByteLeaseAsync(
                    _owner,
                    bytes,
                    _background,
                    cancellationToken,
                    _requestCancellationToken)
                .ConfigureAwait(false);
        }

        public void TrackWork(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            lock (_workGate)
            {
                if (_closed)
                {
                    throw new InvalidOperationException(
                        "The image resolver work-tracking scope is closed.");
                }

                checked { _trackedWorkCount++; }
            }

            _ = ObserveTrackedWorkAsync(completion);
        }

        public void Dispose()
        {
            bool releaseSessionLifetime;
            lock (_workGate)
            {
                if (_closed)
                    return;

                _closed = true;
                releaseSessionLifetime = TryReleaseSessionLifetimeNoLock();
            }

            if (releaseSessionLifetime)
                _session.RetireWork();
        }

        private async Task ObserveTrackedWorkAsync(Task completion)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Losing image branches normally settle through cancellation.
            }
            catch (Exception exception)
            {
                // The task's actual fault is observed, but never log its message
                // or source-bearing state from the caller-owned completion task.
                try
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[MarkdownPerformanceSession] detached image resolver work failed: " +
                        $"{exception.GetType().Name}; HRESULT=0x{exception.HResult:X8}");
                }
                catch
                {
                }
            }
            finally
            {
                CompleteTrackedWork();
            }
        }

        private void CompleteTrackedWork()
        {
            bool releaseSessionLifetime;
            lock (_workGate)
            {
                if (_trackedWorkCount <= 0)
                {
                    throw new InvalidOperationException(
                        "The image resolver work-tracking count is already zero.");
                }

                _trackedWorkCount--;
                releaseSessionLifetime = TryReleaseSessionLifetimeNoLock();
            }

            if (releaseSessionLifetime)
                _session.RetireWork();
        }

        private bool TryReleaseSessionLifetimeNoLock()
        {
            if (!_closed || _trackedWorkCount != 0 || _sessionLifetimeReleased)
                return false;

            _sessionLifetimeReleased = true;
            return true;
        }
    }

    private sealed class SourceKeyComparer : IEqualityComparer<SourceKey>
    {
        internal static readonly SourceKeyComparer Instance = new();

        public bool Equals(SourceKey x, SourceKey y) =>
            ReferenceEquals(x.ResolverIdentity, y.ResolverIdentity) &&
            ReferenceEquals(x.DocumentScope, y.DocumentScope) &&
            Equals(x.Context, y.Context) &&
            string.Equals(x.Source, y.Source, StringComparison.Ordinal);

        public int GetHashCode(SourceKey key) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(key.ResolverIdentity),
            key.DocumentScope is null ? 0 : RuntimeHelpers.GetHashCode(key.DocumentScope),
            key.Context,
            StringComparer.Ordinal.GetHashCode(key.Source));
    }

    private readonly record struct CachedSourceIdentity(long EntryIdentity, int ByteLength);

    private sealed class CachedSource(
        SourceKey key,
        MarkdownImageAsset asset,
        long metadataWeight,
        long identity)
    {
        internal const long RasterIntrinsicMetadataWeightBytes = 16;
        internal readonly SourceKey Key = key;
        internal readonly MarkdownImageAsset Asset = asset;
        internal long MetadataWeight = metadataWeight;
        internal readonly long Identity = identity;
        internal RasterIntrinsicMetadata? RasterIntrinsicMetadata;
        internal LinkedListNode<CachedSource> Node = null!;
    }

    /// <summary>
    /// Keeps source-byte admission and session shutdown accounting alive for the
    /// full lifetime of a transferable image result, not just until the resolver
    /// returns. Cleanup attempts every owned release exactly once.
    /// </summary>
    private sealed class SessionSourceByteLease(
        MarkdownPerformanceSession session,
        IDisposable admissionLease,
        CancellationTokenSource linkedCancellation) : IDisposable
    {
        private MarkdownPerformanceSession? _session = session;
        private IDisposable? _admissionLease = admissionLease;
        private CancellationTokenSource? _linkedCancellation = linkedCancellation;

        public void Dispose()
        {
            MarkdownPerformanceSession? owner = Interlocked.Exchange(ref _session, null);
            if (owner is null)
                return;

            IDisposable? admission = Interlocked.Exchange(ref _admissionLease, null);
            CancellationTokenSource? linked = Interlocked.Exchange(ref _linkedCancellation, null);
            try
            {
                try
                {
                    admission?.Dispose();
                }
                catch (Exception exception)
                {
                    LogLeaseCleanupFailure("source-byte admission", exception);
                }

                try
                {
                    linked?.Dispose();
                }
                catch (Exception exception)
                {
                    LogLeaseCleanupFailure("source-byte cancellation", exception);
                }
            }
            finally
            {
                owner.RetireWork();
            }
        }
    }

    [Conditional("DEBUG")]
    private static void LogLeaseCleanupFailure(string resource, Exception exception)
    {
        try
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] {resource} cleanup failed: {exception.GetType().Name}");
        }
        catch
        {
        }
    }

    private sealed class CpuPreparationLease(
        IDisposable admissionLease,
        MarkdownPerformanceSession session,
        long started) : IDisposable
    {
        private IDisposable? _admissionLease = admissionLease;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
                return;
            try
            {
                held.Dispose();
            }
            finally
            {
                Interlocked.Add(ref session._cpuPreparationMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                session.RetireWork();
            }
        }
    }

    private sealed class RasterWorkLease(MarkdownPerformanceSession session) : IDisposable
    {
        private MarkdownPerformanceSession? _session = session;
        public void Dispose() => Interlocked.Exchange(ref _session, null)?.RetireWork();
    }

    private sealed class ScenePreparationLease(
        IDisposable admissionLease,
        CancellationTokenSource cancellation,
        MarkdownPerformanceSession session,
        long started) : IMarkdownScenePreparationLease
    {
        private IDisposable? _admissionLease = admissionLease;
        private int _timingCompleted;
        public CancellationToken CancellationToken => cancellation.Token;

        internal void CompletePreparationTiming()
        {
            if (Interlocked.Exchange(ref _timingCompleted, 1) == 0)
            {
                Interlocked.Add(ref session._scenePreparationMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
                return;
            try
            {
                held.Dispose();
            }
            finally
            {
                try
                {
                    CompletePreparationTiming();
                }
                finally
                {
                    try
                    {
                        cancellation.Dispose();
                    }
                    finally
                    {
                        session.RetireWork();
                    }
                }
            }
        }
    }
}
