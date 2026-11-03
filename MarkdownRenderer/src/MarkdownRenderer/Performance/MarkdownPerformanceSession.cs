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

/// <summary>
/// Shares bounded image preparation between document views in one security partition.
/// The host owns this service; controls borrow it and own their document scopes.
/// </summary>
public sealed class MarkdownPerformanceSession :
    IMarkdownPerformanceSessionInternal,
    IMarkdownDocumentScopeOpener,
    IDisposable,
    IAsyncDisposable
{
    private readonly BoundedMarkdownLayoutScheduler _layoutWorkScheduler;
    private readonly BoundedMarkdownImageActivationScheduler _imageActivationScheduler = new();
    internal MarkdownCompletedSceneCache CompletedSceneCache { get; }
    private readonly MarkdownPerformanceSessionLifetime _lifetime = new();
    private readonly MarkdownSessionAdmissions _admissions;
    private readonly MarkdownSharedImageFetcher _fetcher;
    private readonly MarkdownSessionImageServices _imageServices;
    private readonly MarkdownDocumentScopeRegistry _documentScopes;
    private readonly bool _memoryPressureSubscribed;
    private readonly object _scenePlansGate = new();
    private readonly SnapshotRetirementTracker _scenePlanRetirements = new();
    private readonly Dictionary<object, MarkdownProgressiveScenePlan> _scenePlans =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<object, RetiredDeferredSceneRegistration> _retiredSceneRegistrations = new();
    private readonly object _styleRoleDemandGate = new();
    private readonly ConditionalWeakTable<MarkdownDocument, Task<MarkdownStyleRoleDemandResult>> _styleRoleDemandCache = new();

    /// <summary>Creates an opt-in session. Use a distinct session for each account/security partition.</summary>
    public MarkdownPerformanceSession(MarkdownPerformanceOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        CompletedSceneCache = new MarkdownCompletedSceneCache(options.SceneCacheBudgetBytes);
        _layoutWorkScheduler = new BoundedMarkdownLayoutScheduler();
        _admissions = new MarkdownSessionAdmissions(_lifetime, options);
        SourceCache = new MarkdownImageSourceCache(_lifetime, options.SourceCacheBudgetBytes);
        _fetcher = new MarkdownSharedImageFetcher(_lifetime, _admissions, SourceCache, options);
        _imageServices = new MarkdownSessionImageServices(
            options, _lifetime, _admissions, SourceCache, _fetcher);
        _documentScopes = new MarkdownDocumentScopeRegistry(
            this, this, _lifetime, options.PrefetchDocumentImages);
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

    // Hosts set this only while profiling. Payloads contain only an
    // in-process owner identity, fixed stage ID, and stopwatch ticks—never source
    // content or resource identifiers. A typed callback avoids runtime event
    // discovery and remains compatible with NativeAOT trimming.
    internal Action<int, int, long>? ParseStageDiagnosticRecorder { get; set; }

    // Admission queue depth is sampled only by internal diagnostics/tests. It
    // adds no work to the CPU-preparation hot path.
    internal int PendingCpuPreparations => _admissions.PendingCpuPreparations;

    // Admitted work that shutdown still waits for. Sampled only by internal
    // diagnostics/tests.
    internal int ActiveWork => _lifetime.ActiveWork;

    // The session-wide image source cache. Internal diagnostics/tests sample
    // its retained-entry and capacity counters.
    internal MarkdownImageSourceCache SourceCache { get; }

    // The renderer checks this once per layout request so stopwatch reads and
    // native-layout counting are absent unless a host explicitly enables layout diagnostics.
    internal bool IsLayoutDiagnosticCaptureEnabled { get; set; }

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

    internal bool IsDisposed => _lifetime.IsDisposed;

    event EventHandler? IMarkdownPerformanceSessionInternal.Disposed
    {
        add
        {
            bool invokeImmediately = !_lifetime.TryAddDisposedHandler(value);
            if (invokeImmediately && value is not null)
                NotifyDisposedHandlers(value);
        }
        remove => _lifetime.RemoveDisposedHandler(value);
    }

    internal long ActiveSourceBytes => _admissions.SourceBytes.ActiveBytes;

    // Internal evidence for cancellation callback failures; see
    // MarkdownPerformanceSessionLifetime.CancellationRetirementFailures.
    internal long CancellationRetirementFailures => _lifetime.CancellationRetirementFailures;

    bool IMarkdownPerformanceSessionInternal.IsDisposed => IsDisposed;
    CancellationToken IMarkdownPerformanceSessionInternal.LifetimeToken => _lifetime.Token;
    IDisposable? IMarkdownPerformanceSessionInternal.TryBeginRasterWork()
    {
        return _lifetime.TryBeginWorkLease();
    }
    bool IMarkdownPerformanceSessionInternal.IsLayoutDiagnosticCaptureEnabled =>
        IsLayoutDiagnosticCaptureEnabled;

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
        if (!_lifetime.TryRegisterWork())
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
            _lifetime.RetireWork();
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
        if (!_lifetime.TryRegisterWork())
            return CreateLayoutWorkFailure<T>(new OperationCanceledException(cancellationToken));

        CancellationTokenSource? linked = null;
        Task<T>? scheduled = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            scheduled = _layoutWorkScheduler.ScheduleAsync(priority, work, linked.Token);
        }
        catch (Exception exception)
        {
            linked?.Dispose();
            _lifetime.RetireWork();
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
        if (cancellationToken.IsCancellationRequested || !_lifetime.TryRegisterWork())
            return false;

        CancellationTokenSource? linked = null;
        ImageActivationCompletion? completion = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
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
                _lifetime.RetireWork();
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
                    session._lifetime.RetireWork();
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
                session._lifetime.RetireWork();
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
        owner.DeferredSceneReadiness.IsSnapshotCurrent(
            expectedSnapshot,
            expectedGeneration,
            expectedLayoutRevision) &&
        ReferenceEquals(owner.PerformanceSession, this);

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
        _documentScopes.Prepare(
            documentOwner,
            source,
            registryRevision,
            document,
            safeHtmlPolicy,
            resolver,
            context);

    void IMarkdownPerformanceSessionInternal.ReleaseDocumentScope(object documentOwner)
        => _documentScopes.Release(documentOwner);

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
        long retainedBytes = SourceCache.RetainedBytes;
        return new MarkdownPerformanceSnapshot(
            retainedBytes,
            SourceCache.Hits,
            _fetcher.Fetches,
            _fetcher.FetchMilliseconds,
            _fetcher.FetchFailures,
            _fetcher.FetchCancellations,
            SourceCache.Evictions,
            _fetcher.PendingFetches,
            _fetcher.ActiveFetches,
            _admissions.CpuPreparations,
            _admissions.CpuPreparationMilliseconds,
            _admissions.ScenePreparations,
            _admissions.ScenePreparationMilliseconds,
            _admissions.SourceBytes.ActiveBytes,
            _admissions.SourceBytes.PeakActiveBytes,
            _admissions.SourceBytes.PendingRequests,
            CompletedSceneCache.RetainedWeightBytes,
            CompletedSceneCache.Hits,
            CompletedSceneCache.Evictions);
    }

    /// <summary>Releases source and completed-scene cache retention; active layouts and visible handoffs are unaffected.</summary>
    public void Trim()
    {
        SourceCache.Trim();
        CompletedSceneCache.Trim();
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args) => Trim();

    internal MarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        return _lifetime.CreateWhileActive(
            this,
            (Session: this, Services: _imageServices, Resolver: resolver, Context: context),
            static state => new MarkdownPerformanceDocumentScope(
                state.Session, state.Services, state.Resolver, state.Context));
    }

    MarkdownPerformanceDocumentScope IMarkdownDocumentScopeOpener.OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => OpenDocument(resolver, context);

    internal ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, exclusive: false, cancellationToken);

    internal ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken) =>
        _admissions.EnterCpuPreparationAsync(documentOwner, exclusive, cancellationToken);

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

    private ValueTask<IDisposable> ReserveSourceByteLeaseAsync(
        object documentOwner,
        long bytes,
        bool background,
        CancellationToken cancellationToken,
        CancellationToken requestCancellationToken) =>
        _admissions.ReserveSourceBytesAsync(
            documentOwner, bytes, background, cancellationToken, requestCancellationToken);

    internal ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        _admissions.EnterScenePreparationAsync(documentOwner, cancellationToken);

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

    /// <inheritdoc />
    public void Dispose()
    {
        Task drain;
        TaskCompletionSource<bool> completed;
        EventHandler? disposedHandlers;
        if (!_lifetime.TryBeginShutdown(out drain, out completed, out disposedHandlers))
            return;

        _layoutWorkScheduler.Dispose();
        _imageActivationScheduler.Dispose();

        ParseStageDiagnosticRecorder = null;
        IsLayoutDiagnosticCaptureEnabled = false;
        ReleaseAllProgressiveScenePlans();
        CompletedSceneCache.Dispose();
        _documentScopes.ReleaseAll();
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
        SourceCache.ClearResolverIdentities();
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
        Task? completion = _lifetime.ShutdownCompletion;
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
            _fetcher.DisposeFetchSlots();
            _lifetime.DisposeCancellation();
        }

        if (failures is null)
            completed.TrySetResult(true);
        else if (failures.Count == 1)
            completed.TrySetException(failures[0]);
        else
            completed.TrySetException(new AggregateException(failures));
    }

}
