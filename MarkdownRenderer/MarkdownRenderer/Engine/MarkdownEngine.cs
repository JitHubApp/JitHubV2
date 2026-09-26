using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer;

/// <summary>
/// Immutable markdown parser configuration with thread-safe parse admission and
/// caching. One engine may parse documents concurrently and its documents may
/// be shared by multiple views. Custom extension callbacks and their captured
/// services must support concurrent invocation.
/// </summary>
public sealed class MarkdownEngine : IDisposable
{
    /// <summary>Default completed-document cache budget: 16 MiB.</summary>
    public const long DefaultParseCacheBudgetBytes = 16L * 1024 * 1024;

    private const long BaseDocumentWeightBytes = 1024;
    private const long SemanticEntryWeightBytes = 64;
    private const long SourceMapEntryWeightBytes = 40;
    private const long Utf16SyntaxWeightPerCharacter = 6;
    private const long ExtensionMapEntryWeightBytes = 48;
    private const long ExtensionFragmentWeightBytes = 32;
    private const long ExtensionContentWeightBytes = 112;
    private const long ExtensionAttributeWeightBytes = 48;
    private const long VectorSceneWeightBytes = 64;
    private const long VectorCommandWeightBytes = 72;
    private const long VectorPathOperationWeightBytes = 56;
    private const long VectorStyleWeightBytes = 64;
    private const long VectorSemanticWeightBytes = 80;
    private const long VectorLinkWeightBytes = 40;

    private static readonly IReadOnlyList<MarkdownDiagnostic> _noDiagnostics =
        Array.AsReadOnly(Array.Empty<MarkdownDiagnostic>());

    private readonly Dictionary<SourceKey, CompletedParse> _completedParses = new(SourceKeyComparer.Instance);
    private readonly LinkedList<CompletedParse> _completedRecency = new();
    private readonly Dictionary<SourceKey, InFlightParse> _inFlightParses = new(SourceKeyComparer.Instance);
    private readonly LinkedList<InFlightParse> _queuedParses = new();
    private readonly MarkdownPipeline _pipeline;
    private readonly IReadOnlyList<IDisposable> _ownedResources;
    private readonly IReadOnlyList<Action<MarkdownPipelineBuilder>> _pipelineConfigurations;
    private readonly MarkdownExtensionCallbackLifetime? _extensionCallbackLifetime;
    private readonly bool _isSharedSingleton;
    private MarkdownPipeline? _specificationValidationPipeline;
    private readonly object _parseGate = new();
    private int _activeParseCount;
    private int _pendingCancellationRetirementCount;
    private int _outstandingParseCount;
    private long _outstandingSourceBytes;
    private long _completedParseWeightBytes;
    private long _sourceKeyHashCount;
    private long _parseGeneration;
    private CompletedParse? _lastCompletedParse;
    private WeakReference<string>? _lastCompletedSourceIdentity;
    private bool _disposeRequested;
    private bool _extensionCallbacksRetired;
    private bool _ownedResourcesDisposed;

    internal MarkdownEngine(
        MarkdownProfile profile,
        MarkdownExtensionSet extensions,
        IReadOnlyList<IDisposable> ownedResources,
        MarkdownExtensionCallbackLifetime? extensionCallbackLifetime,
        IReadOnlyList<Action<MarkdownPipelineBuilder>> pipelineConfigurations,
        long parseCacheBudgetBytes,
        MarkdownParseLimits parseLimits,
        IMarkdownPresentationConfiguration? presentationConfiguration,
        bool isSharedSingleton)
    {
        Profile = profile;
        Extensions = extensions;
        ParseCacheBudgetBytes = parseCacheBudgetBytes;
        ParseLimits = parseLimits;
        PresentationConfiguration = presentationConfiguration;
        _isSharedSingleton = isSharedSingleton;
        _ownedResources = ownedResources ?? throw new ArgumentNullException(nameof(ownedResources));
        _pipelineConfigurations = pipelineConfigurations ??
            throw new ArgumentNullException(nameof(pipelineConfigurations));
        _extensionCallbackLifetime = extensionCallbackLifetime;
        _extensionCallbacksRetired = extensionCallbackLifetime is null;
        _pipeline = BuildPipeline(profile, extensions, _pipelineConfigurations);
    }

    /// <summary>Gets an engine configured for strict CommonMark.</summary>
    public static MarkdownEngine Default { get; } = new MarkdownEngineBuilder().BuildShared();

    /// <summary>Gets the immutable parsing profile used by this engine.</summary>
    public MarkdownProfile Profile { get; }

    /// <summary>Gets the immutable declarative extension set used by this engine.</summary>
    public MarkdownExtensionSet Extensions { get; }

    /// <summary>Gets the byte budget for completed parsed documents.</summary>
    public long ParseCacheBudgetBytes { get; }

    /// <summary>Gets the bounded admission limits for uncached parses.</summary>
    public MarkdownParseLimits ParseLimits { get; }

    /// <summary>
    /// Gets the frozen UI-package presentation configuration without exposing
    /// WinUI or layout types from MarkdownRenderer.Core.
    /// </summary>
    internal IMarkdownPresentationConfiguration? PresentationConfiguration { get; }

    /// <summary>
    /// Creates a reusable builder initialized from this engine. Opaque
    /// presentation registrations are preserved without exposing UI types.
    /// Owned extension services are recreated independently for each new engine.
    /// </summary>
    public MarkdownEngineBuilder ToBuilder() => new(this);

    internal int ActiveParseCount
    {
        get
        {
            lock (_parseGate)
                return _activeParseCount;
        }
    }

    internal IReadOnlyList<Action<MarkdownPipelineBuilder>> PipelineConfigurations =>
        _pipelineConfigurations;

    internal int CompletedParseCount
    {
        get
        {
            lock (_parseGate)
                return _completedParses.Count;
        }
    }

    internal int QueuedParseCount
    {
        get
        {
            lock (_parseGate)
                return _queuedParses.Count;
        }
    }

    internal int OutstandingParseCount
    {
        get
        {
            lock (_parseGate)
                return _outstandingParseCount;
        }
    }

    /// <summary>
    /// Gets the number of content hashes computed for parse-cache keys. This is
    /// internal evidence for cache-path tests; callers should not depend on it.
    /// </summary>
    internal long SourceKeyHashCount => Interlocked.Read(ref _sourceKeyHashCount);

    /// <summary>
    /// Parses markdown away from the calling context and returns an immutable,
    /// reusable document. Source and source spans are preserved as UTF-16.
    /// </summary>
    /// <param name="source">Markdown source. A null value is treated as empty.</param>
    /// <param name="cancellationToken">Token used to cancel queued or post-parse document work.</param>
    public Task<MarkdownDocument> ParseAsync(
        string? source,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<MarkdownDocument>(cancellationToken);

        string sourceSnapshot = source ?? string.Empty;
        if (sourceSnapshot.Length > ParseLimits.MaximumSourceLength)
        {
            return Task.FromException<MarkdownDocument>(new ArgumentOutOfRangeException(
                nameof(source),
                $"Markdown source cannot exceed {ParseLimits.MaximumSourceLength} UTF-16 code units."));
        }

        long sourceBytes = checked((long)sourceSnapshot.Length * sizeof(char));

        // The overwhelmingly common renderer path assigns the exact same
        // immutable string instance repeatedly. Check the most recent completed
        // lookup by identity before computing String.GetHashCode, which is
        // O(source length) and otherwise makes a 10 MiB cache hit cost several
        // milliseconds. The entry pointer never outlives its admitted cache
        // entry, and the lookup source is held weakly so an equal but distinct
        // source cannot extend cache retention or escape the configured budget.
        // With completed caching disabled there can never be an identity hit,
        // so avoid an otherwise redundant lock before the normal admission
        // lock. This keeps cache-disabled and concurrent miss workloads from
        // paying two serialized gate acquisitions per request.
        if (ParseCacheBudgetBytes > 0)
        {
            lock (_parseGate)
            {
                if (_disposeRequested)
                {
                    return Task.FromException<MarkdownDocument>(
                        new ObjectDisposedException(nameof(MarkdownEngine)));
                }

                if (TryGetLastCompletedParseBySourceIdentity(
                        sourceSnapshot,
                        out var identityCachedDocument))
                {
                    return Task.FromResult(identityCachedDocument);
                }
            }
        }

        SourceKey sourceKey = CreateSourceKey(sourceSnapshot);
        InFlightParse inFlight;
        bool startParse = false;
        lock (_parseGate)
        {
            if (_disposeRequested)
            {
                return Task.FromException<MarkdownDocument>(
                    new ObjectDisposedException(nameof(MarkdownEngine)));
            }

            if (TryGetCompletedParse(sourceKey, out var cachedDocument))
                return Task.FromResult(cachedDocument);

            if (!_inFlightParses.TryGetValue(sourceKey, out inFlight!) || inFlight.IsAbandoned)
            {
                if (_outstandingParseCount >= ParseLimits.MaximumOutstandingParseCount ||
                    _outstandingSourceBytes > ParseLimits.MaximumOutstandingSourceBytes - sourceBytes)
                {
                    return Task.FromException<MarkdownDocument>(new InvalidOperationException(
                        "The markdown parse admission limit is full. Retry after an outstanding parse completes."));
                }

                inFlight = new InFlightParse(
                    sourceKey,
                    sourceBytes,
                    unchecked(++_parseGeneration));
                _inFlightParses[sourceKey] = inFlight;
                _outstandingParseCount++;
                _outstandingSourceBytes += sourceBytes;
                ObserveFault(inFlight.ParseTask);

                if (_activeParseCount < ParseLimits.MaximumConcurrentParseCount)
                {
                    inFlight.IsStarted = true;
                    _activeParseCount++;
                    startParse = true;
                }
                else
                {
                    inFlight.QueueNode = _queuedParses.AddLast(inFlight);
                }
            }

            inFlight.WaiterCount++;
        }

        if (startParse)
            StartParse(inFlight);

        return AwaitParseAsync(inFlight, cancellationToken);
    }

    internal static long EstimateDocumentWeight(MarkdownDocument document)
        => EstimateDocumentWeight(document, CancellationToken.None);

    private static long EstimateDocumentWeight(
        MarkdownDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        long weight = BaseDocumentWeightBytes;
        weight = AddSaturating(weight, (long)document.Source.Length * Utf16SyntaxWeightPerCharacter);
        weight = AddSaturating(weight, (long)document.SourceMap.Count * SourceMapEntryWeightBytes);

        foreach (var heading in document.GetHeadings())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, heading.DisplayText);
        }
        foreach (var link in document.GetLinks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, link.DisplayText, link.Url, link.Title);
        }
        foreach (var codeBlock in document.GetCodeBlocks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, codeBlock.DisplayText, codeBlock.Language);
        }
        foreach (var image in document.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, image.DisplayText, image.Source, image.AltText, image.Title);
        }
        foreach (var footnote in document.GetFootnotes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, footnote.Label, footnote.DisplayText);
        }
        foreach (var definition in document.GetDefinitionItems())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, definition.Term, definition.Definition);
        }
        foreach (var abbreviation in document.GetAbbreviations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, abbreviation.DisplayText, abbreviation.Expansion);
        }
        foreach (var fragment in document.GetFragments())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, fragment.Id);
        }
        foreach (var diagnostic in document.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, diagnostic.Code, diagnostic.Message);
        }

        AddExtensionContent(
            ref weight,
            document.ExtensionBlockNodeContent,
            cancellationToken);
        AddExtensionContent(
            ref weight,
            document.ExtensionInlineNodeContent,
            cancellationToken);
        weight = AddSaturating(
            weight,
            (long)(document.ExtensionBlockContent.Count + document.ExtensionInlineContent.Count) *
            ExtensionMapEntryWeightBytes);

        return weight;
    }

    private async Task<MarkdownDocument> AwaitParseAsync(
        InFlightParse inFlight,
        CancellationToken cancellationToken)
    {
        bool receivedDocument = false;
        try
        {
            if (!cancellationToken.CanBeCanceled)
            {
                MarkdownDocument document = await inFlight.ParseTask.ConfigureAwait(false);
                receivedDocument = true;
                return document;
            }

            MarkdownDocument cancelableDocument = await inFlight.ParseTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            receivedDocument = true;
            return cancelableDocument;
        }
        finally
        {
            ReleaseWaiter(inFlight, receivedDocument);
        }
    }

    private void StartParse(InFlightParse inFlight)
    {
        _ = Task.Run(() => ExecuteParseAsync(inFlight), CancellationToken.None);
    }

    private async Task ExecuteParseAsync(InFlightParse inFlight)
    {
        MarkdownDocument? document = null;
        long documentWeight = 0;
        bool publish = false;
        Exception? failure = null;
        try
        {
            document = await ParseCoreAsync(inFlight.Source, inFlight.CancellationToken)
                .ConfigureAwait(false);
            inFlight.CancellationToken.ThrowIfCancellationRequested();
            if (ParseCacheBudgetBytes > 0)
            {
                long minimumWeight = AddSaturating(
                    BaseDocumentWeightBytes,
                    (long)document.Source.Length * Utf16SyntaxWeightPerCharacter);
                documentWeight = minimumWeight > ParseCacheBudgetBytes
                    ? minimumWeight
                    : EstimateDocumentWeight(document, inFlight.CancellationToken);
            }
            inFlight.CancellationToken.ThrowIfCancellationRequested();
            publish = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        InFlightParse? next = null;
        CancellationToken operationToken = inFlight.CancellationToken;
        Task cancellationRetirement = inFlight.RetireCancellation();
        TrackCancellationRetirement(cancellationRetirement);
        try
        {
            next = CompleteInFlight(
                inFlight,
                publish ? document : null,
                documentWeight);
            if (next is not null)
                StartParse(next);

            DisposeOwnedResourcesIfReady();
        }
        catch (Exception cleanupFailure)
        {
            // Coordination/disposal failures must never strand every waiter on
            // an uncompleted task. Preserve a parse failure when one already
            // exists; otherwise surface the cleanup failure deterministically.
            failure ??= cleanupFailure;
        }
        finally
        {
            if (failure is OperationCanceledException && operationToken.IsCancellationRequested)
                inFlight.Completion.TrySetCanceled(operationToken);
            else if (failure is not null)
                inFlight.Completion.TrySetException(failure);
            else
                inFlight.Completion.TrySetResult(document!);
        }
    }

    private async ValueTask<MarkdownDocument> ParseCoreAsync(
        string source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isGfm029 =
            (Profile.FeatureFlags & MarkdownProfileFeatures.Gfm029Marker) != 0;
        Gfm029Compatibility.PreparedSource prepared = isGfm029
            ? Gfm029Compatibility.PrepareSource(
                source,
                enableTables:
                    (Profile.FeatureFlags & MarkdownProfileFeatures.PipeTables) != 0)
            : Gfm029Compatibility.PreparedSource.Unchanged(source);
        var parsedDocument = Markdown.Parse(prepared.Text, _pipeline);
        if (isGfm029)
        {
            Gfm029Compatibility.NormalizeSyntaxTree(
                parsedDocument,
                source,
                enableAutoLinks:
                    (Profile.FeatureFlags & MarkdownProfileFeatures.AutoLinks) != 0,
                prepared: prepared);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await MarkdownDocument.FromParsedAsync(
                source,
                parsedDocument,
                _noDiagnostics,
                Extensions,
                cancellationToken,
                PresentationConfiguration)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Renders with the selected profile's specification syntax while allowing
    /// raw HTML passthrough solely for the official-example conformance oracle.
    /// Production parsing continues to enforce the profile's literal-HTML policy.
    /// </summary>
    internal string RenderSpecificationHtmlForValidation(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MarkdownPipeline pipeline = _specificationValidationPipeline ??=
            BuildPipeline(
                Profile,
                MarkdownExtensionSet.Empty,
                _pipelineConfigurations,
                enforceHtmlPolicy: false);
        bool isGfm029 =
            (Profile.FeatureFlags & MarkdownProfileFeatures.Gfm029Marker) != 0;
        Gfm029Compatibility.PreparedSource prepared = isGfm029
            ? Gfm029Compatibility.PrepareSource(
                source,
                enableTables:
                    (Profile.FeatureFlags & MarkdownProfileFeatures.PipeTables) != 0)
            : Gfm029Compatibility.PreparedSource.Unchanged(source);
        var parsedDocument = Markdown.Parse(prepared.Text, pipeline);
        if (isGfm029)
        {
            Gfm029Compatibility.NormalizeSyntaxTree(
                parsedDocument,
                source,
                enableAutoLinks:
                    (Profile.FeatureFlags & MarkdownProfileFeatures.AutoLinks) != 0,
                prepared: prepared);
        }
        return Gfm029Compatibility.RenderReferenceHtml(
            parsedDocument,
            pipeline,
            applyTagFilter:
                (Profile.FeatureFlags & MarkdownProfileFeatures.GfmTagFilter) != 0);
    }

    private void ReleaseWaiter(InFlightParse inFlight, bool receivedDocument)
    {
        bool cancelAbandonedWork = false;
        bool completeQueuedCancellation = false;
        CancellationToken queuedCancellationToken = default;
        lock (_parseGate)
        {
            if (inFlight.WaiterCount > 0)
                inFlight.WaiterCount--;
            if (receivedDocument)
                inFlight.SuccessfulWaiterCount++;

            if (inFlight.WaiterCount != 0)
                return;

            if (inFlight.IsCompleted)
            {
                // Completion and caller cancellation can race after the parsed
                // document has been admitted but before the completion source
                // is published. If every waiter observed cancellation/failure,
                // retire only the entry created by this operation. A newer
                // same-source parse must never be evicted by the older caller.
                if (inFlight.SuccessfulWaiterCount == 0 &&
                    _completedParses.TryGetValue(inFlight.Key, out CompletedParse? completed) &&
                    completed.ParseGeneration == inFlight.ParseGeneration &&
                    !completed.WasObserved)
                {
                    RemoveCompletedParse(completed);
                }
                return;
            }

            if (inFlight.IsAbandoned)
                return;

            inFlight.IsAbandoned = true;
            if (_inFlightParses.TryGetValue(inFlight.Key, out var current) &&
                ReferenceEquals(current, inFlight))
            {
                _inFlightParses.Remove(inFlight.Key);
            }

            if (!inFlight.IsStarted)
            {
                if (inFlight.QueueNode is not null)
                {
                    _queuedParses.Remove(inFlight.QueueNode);
                    inFlight.QueueNode = null;
                }

                inFlight.IsCompleted = true;
                _outstandingParseCount--;
                _outstandingSourceBytes -= inFlight.SourceBytes;
                queuedCancellationToken = inFlight.CancellationToken;
                completeQueuedCancellation = true;
            }
            else
            {
                cancelAbandonedWork = true;
            }
        }

        // The operation was already removed and marked abandoned under the
        // gate, so it cannot publish while it unwinds. CancelAsync marks the
        // token immediately but keeps extension registrations off this caller.
        if (cancelAbandonedWork)
            inFlight.RequestCancellation();
        else if (completeQueuedCancellation)
            inFlight.RequestCancellation();

        if (completeQueuedCancellation)
        {
            inFlight.Completion.TrySetCanceled(queuedCancellationToken);
            _ = inFlight.RetireCancellation();
        }
    }

    private InFlightParse? CompleteInFlight(
        InFlightParse inFlight,
        MarkdownDocument? document,
        long documentWeight)
    {
        lock (_parseGate)
        {
            bool isCurrent =
                _inFlightParses.TryGetValue(inFlight.Key, out var current) &&
                ReferenceEquals(current, inFlight);
            if (isCurrent)
                _inFlightParses.Remove(inFlight.Key);

            inFlight.IsCompleted = true;
            _activeParseCount--;
            _outstandingParseCount--;
            _outstandingSourceBytes -= inFlight.SourceBytes;
            if (document is not null &&
                isCurrent &&
                !inFlight.IsAbandoned &&
                !inFlight.CancellationToken.IsCancellationRequested)
            {
                AddCompletedParse(
                    inFlight.Key,
                    document,
                    documentWeight,
                    inFlight.ParseGeneration);
            }

            while (!_disposeRequested && _queuedParses.First is { } first)
            {
                _queuedParses.RemoveFirst();
                InFlightParse next = first.Value;
                next.QueueNode = null;
                if (next.IsAbandoned || next.IsCompleted)
                    continue;

                next.IsStarted = true;
                _activeParseCount++;
                return next;
            }

            return null;
        }
    }

    /// <summary>
    /// Releases services created by optional feature convenience overloads.
    /// Disposal cancels admitted work and defers resource release until running
    /// extension work and cancellation registrations have exited. Ordinary
    /// engines reject subsequent parses after disposal. The process-wide
    /// <see cref="Default"/> and feature-pack shared engines explicitly ignore
    /// disposal.
    /// </summary>
    public void Dispose()
    {
        if (_isSharedSingleton)
            return;

        List<InFlightParse>? queued = null;
        List<InFlightParse>? running = null;
        Task? extensionCallbackRetirement = null;
        lock (_parseGate)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;
            extensionCallbackRetirement = _extensionCallbackLifetime?.BeginDispose();
            if (extensionCallbackRetirement is null || extensionCallbackRetirement.IsCompleted)
                _extensionCallbacksRetired = true;
            _completedParses.Clear();
            _completedRecency.Clear();
            _lastCompletedParse = null;
            _lastCompletedSourceIdentity = null;
            _completedParseWeightBytes = 0;

            foreach (InFlightParse operation in _inFlightParses.Values)
            {
                operation.IsAbandoned = true;
                if (operation.IsStarted)
                {
                    (running ??= new List<InFlightParse>()).Add(operation);
                    continue;
                }

                operation.IsCompleted = true;
                operation.QueueNode = null;
                _outstandingParseCount--;
                _outstandingSourceBytes -= operation.SourceBytes;
                (queued ??= new List<InFlightParse>()).Add(operation);
            }

            _inFlightParses.Clear();
            _queuedParses.Clear();
        }

        if (extensionCallbackRetirement is { IsCompleted: false })
        {
            _ = extensionCallbackRetirement.ContinueWith(
                static (_, state) => ((MarkdownEngine)state!).OnExtensionCallbacksRetired(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        CancelOperations(running, complete: false);
        CancelOperations(queued, complete: true);
        DisposeOwnedResourcesIfReady();
    }

    private static void CancelOperations(List<InFlightParse>? operations, bool complete)
    {
        if (operations is null)
            return;

        foreach (InFlightParse operation in operations)
        {
            CancellationToken token = operation.CancellationToken;
            operation.RequestCancellation();

            if (!complete)
                continue;

            operation.Completion.TrySetCanceled(token);
            _ = operation.RetireCancellation();
        }
    }

    private void TrackCancellationRetirement(Task retirement)
    {
        if (retirement.IsCompleted)
            return;

        lock (_parseGate)
            _pendingCancellationRetirementCount++;

        _ = retirement.ContinueWith(
            static (_, state) => ((MarkdownEngine)state!).OnCancellationRetired(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnCancellationRetired()
    {
        lock (_parseGate)
            _pendingCancellationRetirementCount--;

        DisposeOwnedResourcesIfReady();
    }

    private void OnExtensionCallbacksRetired()
    {
        lock (_parseGate)
            _extensionCallbacksRetired = true;

        DisposeOwnedResourcesIfReady();
    }

    private void DisposeOwnedResourcesIfReady()
    {
        IReadOnlyList<IDisposable>? resources = null;
        lock (_parseGate)
        {
            if (!_disposeRequested ||
                _ownedResourcesDisposed ||
                _activeParseCount != 0 ||
                _pendingCancellationRetirementCount != 0 ||
                !_extensionCallbacksRetired)
                return;

            _ownedResourcesDisposed = true;
            resources = _ownedResources;
        }

        for (int index = resources.Count - 1; index >= 0; index--)
        {
            try
            {
                resources[index].Dispose();
            }
            catch
            {
                // Cleanup is best-effort and all owners must be visited. Feature
                // services expose their own diagnostics before engine teardown.
            }
        }
    }

    private bool TryGetCompletedParse(SourceKey sourceKey, out MarkdownDocument document)
    {
        if (_completedParses.TryGetValue(sourceKey, out var cached))
        {
            TouchCompletedParse(cached, sourceKey.Source);
            document = cached.Document;
            return true;
        }

        document = null!;
        return false;
    }

    private bool TryGetLastCompletedParseBySourceIdentity(
        string source,
        out MarkdownDocument document)
    {
        CompletedParse? cached = _lastCompletedParse;
        if (cached is not null &&
            _lastCompletedSourceIdentity is { } identity &&
            identity.TryGetTarget(out string? lastSource) &&
            ReferenceEquals(lastSource, source))
        {
            TouchCompletedParse(cached, source);
            document = cached.Document;
            return true;
        }

        document = null!;
        return false;
    }

    private void TouchCompletedParse(CompletedParse completed, string lookupSource)
    {
        completed.WasObserved = true;
        if (!ReferenceEquals(_completedRecency.First, completed.RecencyNode))
        {
            _completedRecency.Remove(completed.RecencyNode);
            _completedRecency.AddFirst(completed.RecencyNode);
        }

        SetLastCompletedParse(completed, lookupSource);
    }

    private void SetLastCompletedParse(CompletedParse completed, string lookupSource)
    {
        _lastCompletedParse = completed;
        if (_lastCompletedSourceIdentity is null)
            _lastCompletedSourceIdentity = new WeakReference<string>(lookupSource);
        else
            _lastCompletedSourceIdentity.SetTarget(lookupSource);
    }

    private void AddCompletedParse(
        SourceKey sourceKey,
        MarkdownDocument document,
        long weight,
        long parseGeneration)
    {
        if (ParseCacheBudgetBytes == 0)
            return;

        if (weight > ParseCacheBudgetBytes)
            return;

        if (_completedParses.TryGetValue(sourceKey, out var existing))
            RemoveCompletedParse(existing);

        while (_completedParseWeightBytes > ParseCacheBudgetBytes - weight)
        {
            var leastRecent = _completedRecency.Last;
            if (leastRecent is null)
                break;
            RemoveCompletedParse(leastRecent.Value);
        }

        var completed = new CompletedParse(sourceKey, document, weight, parseGeneration);
        completed.RecencyNode = _completedRecency.AddFirst(completed);
        _completedParses.Add(sourceKey, completed);
        _completedParseWeightBytes += weight;
        SetLastCompletedParse(completed, sourceKey.Source);
    }

    private void RemoveCompletedParse(CompletedParse completed)
    {
        _completedParses.Remove(completed.Key);
        _completedRecency.Remove(completed.RecencyNode);
        _completedParseWeightBytes -= completed.WeightBytes;
        if (ReferenceEquals(_lastCompletedParse, completed))
        {
            _lastCompletedParse = null;
            _lastCompletedSourceIdentity = null;
        }
    }

    private SourceKey CreateSourceKey(string source)
    {
        Interlocked.Increment(ref _sourceKeyHashCount);
        return SourceKey.Create(source);
    }

    private static void ObserveFault(Task<MarkdownDocument> parseTask)
    {
        _ = parseTask.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void AddSemanticEntry(
        ref long weight,
        string? first = null,
        string? second = null,
        string? third = null,
        string? fourth = null)
    {
        weight = AddSaturating(weight, SemanticEntryWeightBytes);
        AddStringWeight(ref weight, first);
        AddStringWeight(ref weight, second);
        AddStringWeight(ref weight, third);
        AddStringWeight(ref weight, fourth);
    }

    private static void AddExtensionContent<TKey>(
        ref long weight,
        IReadOnlyDictionary<TKey, MarkdownContentFragment> contentByNode,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        foreach (var entry in contentByNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            weight = AddSaturating(weight, ExtensionMapEntryWeightBytes);
            weight = AddSaturating(weight, ExtensionFragmentWeightBytes);

            // Iterative traversal keeps extension-controlled nesting from
            // consuming the managed call stack during cache accounting.
            var pending = new Stack<MarkdownContent>(entry.Value.Items.Count);
            for (int index = entry.Value.Items.Count - 1; index >= 0; index--)
                pending.Push(entry.Value.Items[index]);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MarkdownContent node = pending.Pop();
                weight = AddSaturating(weight, ExtensionContentWeightBytes);
                AddStringWeight(ref weight, node.StyleRole.Name);
                AddStringWeight(ref weight, node.Text);
                AddStringWeight(ref weight, node.Destination);
                AddStringWeight(ref weight, node.Language);
                AddStringWeight(ref weight, node.FactoryKey);
                AddStringWeight(ref weight, node.CustomKind);
                AddStringWeight(ref weight, node.AccessibilityName);
                AddStringWeight(ref weight, node.AccessibilityDescription);
                AddStringWeight(ref weight, node.SemanticText);
                if (node.VectorScene is { } vectorScene)
                    AddVectorSceneWeight(ref weight, vectorScene, cancellationToken);

                foreach (var attribute in node.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    weight = AddSaturating(weight, ExtensionAttributeWeightBytes);
                    AddStringWeight(ref weight, attribute.Key);
                    AddStringWeight(ref weight, attribute.Value);
                }

                for (int index = node.Children.Count - 1; index >= 0; index--)
                    pending.Push(node.Children[index]);
            }
        }
    }

    private static void AddStringWeight(ref long weight, string? value)
    {
        if (value is not null)
            weight = AddSaturating(weight, (long)value.Length * sizeof(char));
    }

    private static void AddVectorSceneWeight(
        ref long weight,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        weight = AddSaturating(weight, VectorSceneWeightBytes);
        for (int commandIndex = 0; commandIndex < scene.Commands.Count; commandIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorCommand command = scene.Commands[commandIndex];
            weight = AddSaturating(weight, VectorCommandWeightBytes);
            weight = AddSaturating(
                weight,
                (long)command.Path.Count * VectorPathOperationWeightBytes);
            AddStringWeight(ref weight, command.Text);
            if (command.TextStyle is { } textStyle)
                AddStringWeight(ref weight, textStyle.FontFamily);
            if (command.Style is { } style)
            {
                weight = AddSaturating(weight, VectorStyleWeightBytes);
                weight = AddSaturating(weight, (long)style.DashArray.Count * sizeof(float));
            }
        }

        for (int semanticIndex = 0; semanticIndex < scene.Semantics.Count; semanticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem semantic = scene.Semantics[semanticIndex];
            weight = AddSaturating(weight, VectorSemanticWeightBytes);
            AddStringWeight(ref weight, semantic.Name);
            AddStringWeight(ref weight, semantic.Description);
        }

        for (int linkIndex = 0; linkIndex < scene.Links.Count; linkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorLinkAction link = scene.Links[linkIndex];
            weight = AddSaturating(weight, VectorLinkWeightBytes);
            AddStringWeight(ref weight, link.Target);
            AddStringWeight(ref weight, link.Action);
        }
    }

    private static long AddSaturating(long left, long right)
        => right >= long.MaxValue - left ? long.MaxValue : left + right;

    private static MarkdownPipeline BuildPipeline(
        MarkdownProfile profile,
        MarkdownExtensionSet extensions,
        IReadOnlyList<Action<MarkdownPipelineBuilder>> pipelineConfigurations,
        bool enforceHtmlPolicy = true)
    {
        var features = profile.FeatureFlags;
        var builder = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation();

        if ((features & MarkdownProfileFeatures.PipeTables) != 0)
        {
            builder.UsePipeTables(new PipeTableOptions
            {
                UseHeaderForColumnCount = true,
            });
        }
        if ((features & MarkdownProfileFeatures.TaskLists) != 0)
            builder.UseTaskLists();
        if ((features & MarkdownProfileFeatures.AutoLinks) != 0)
            builder.UseAutoLinks();
        if ((features & MarkdownProfileFeatures.Strikethrough) != 0)
            builder.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        if ((features & MarkdownProfileFeatures.Footnotes) != 0)
            builder.UseFootnotes();
        if ((features & MarkdownProfileFeatures.Emoji) != 0)
            builder.UseEmojiAndSmiley(enableSmileys: false);
        if ((features & MarkdownProfileFeatures.DefinitionLists) != 0)
            builder.UseDefinitionLists();
        if ((features & MarkdownProfileFeatures.Abbreviations) != 0)
            builder.UseAbbreviations();
        if ((features & MarkdownProfileFeatures.Figures) != 0)
            builder.UseFigures();
        if ((features & MarkdownProfileFeatures.GenericAttributes) != 0)
            builder.UseGenericAttributes();
        if (extensions.HasFeature(MarkdownExtensionFeatures.DollarMath))
            builder.UseMathematics();

        // GitHub alerts intentionally remain quote blocks here. The native
        // GitHub feature pack recognizes their marker and contributes the
        // declarative renderer without making the core AST public.
        if (enforceHtmlPolicy && profile.HtmlMode == MarkdownHtmlMode.Literal)
            builder.DisableHtml();

        for (int index = 0; index < pipelineConfigurations.Count; index++)
            pipelineConfigurations[index](builder);

        return builder.Build();
    }

    private sealed class CompletedParse
    {
        internal CompletedParse(
            SourceKey key,
            MarkdownDocument document,
            long weightBytes,
            long parseGeneration)
        {
            Key = key;
            Document = document;
            WeightBytes = weightBytes;
            ParseGeneration = parseGeneration;
        }

        internal MarkdownDocument Document { get; }

        internal SourceKey Key { get; }

        internal long ParseGeneration { get; }

        internal LinkedListNode<CompletedParse> RecencyNode { get; set; } = null!;

        internal long WeightBytes { get; }

        internal bool WasObserved { get; set; }
    }

    private sealed class InFlightParse
    {
        private readonly object _cancellationGate = new();
        private CancellationTokenSource? _cancellation = new();
        private Task _cancellationCallbacks = Task.CompletedTask;
        private Task _cancellationRetirement = Task.CompletedTask;

        internal InFlightParse(SourceKey key, long sourceBytes, long parseGeneration)
        {
            Key = key;
            SourceBytes = sourceBytes;
            ParseGeneration = parseGeneration;
            CancellationToken = _cancellation.Token;
        }

        internal CancellationToken CancellationToken { get; }

        internal bool IsAbandoned { get; set; }

        internal bool IsCompleted { get; set; }

        internal bool IsStarted { get; set; }

        internal SourceKey Key { get; }

        internal TaskCompletionSource<MarkdownDocument> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<MarkdownDocument> ParseTask => Completion.Task;

        internal long ParseGeneration { get; }

        internal LinkedListNode<InFlightParse>? QueueNode { get; set; }

        internal string Source => Key.Source;

        internal long SourceBytes { get; }

        internal int SuccessfulWaiterCount { get; set; }

        internal int WaiterCount { get; set; }

        internal void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellation is null || CancellationToken.IsCancellationRequested)
                    return;

                _cancellationCallbacks =
                    CancellationTokenSourceRetirement.RequestCancellation(_cancellation);
            }
        }

        internal Task RetireCancellation()
        {
            lock (_cancellationGate)
            {
                CancellationTokenSource? cancellation = _cancellation;
                if (cancellation is null)
                    return _cancellationRetirement;

                _cancellation = null;
                _cancellationRetirement = CancellationTokenSourceRetirement.DisposeAfter(
                    cancellation,
                    _cancellationCallbacks);
                return _cancellationRetirement;
            }
        }
    }

    private readonly record struct SourceKey(string Source, int OrdinalHashCode)
    {
        internal static SourceKey Create(string source) =>
            new(source, StringComparer.Ordinal.GetHashCode(source));
    }

    private sealed class SourceKeyComparer : IEqualityComparer<SourceKey>
    {
        internal static SourceKeyComparer Instance { get; } = new();

        public bool Equals(SourceKey x, SourceKey y) =>
            x.OrdinalHashCode == y.OrdinalHashCode &&
            (ReferenceEquals(x.Source, y.Source) ||
             string.Equals(x.Source, y.Source, StringComparison.Ordinal));

        public int GetHashCode(SourceKey value) => value.OrdinalHashCode;
    }
}
