using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using RendererDocument = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.Performance;

internal sealed class MarkdownDeferredSceneResult(
    MarkdownContentFragment fragment,
    IReadOnlyList<MarkdownDiagnostic> diagnostics)
{
    internal MarkdownContentFragment Fragment { get; } = fragment;
    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Control-owned viewport work plan for deferred block scenes. The plan is
/// built once from the immutable parsed document, then visible boxes resolve
/// their private marker without rescanning the syntax tree.
/// </summary>
internal sealed class MarkdownProgressiveScenePlan : IDisposable
{
    // Math and Mermaid feature packs use this private, serializable content
    // attribute to mark a code fallback. The marker value is
    // "<exact syntax kind>|<document-unique token>". It is metadata only; the
    // source text and source span remain the fallback and diagnostic authority.
    internal const string DeferredSceneMarkerAttribute = "renderer.internal.deferred-scene";

    private const char MarkerSeparator = '|';

    private readonly object _gate = new();
    private readonly RendererDocument _document;
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly object _documentOwner;
    private readonly IReadOnlyDictionary<string, DeferredSceneWork> _works;
    private readonly Dictionary<string, MarkdownDeferredSceneResult> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _active = new(StringComparer.Ordinal);
    private CancellationTokenSource _generationCancellation = new();
    private IReadOnlyList<MarkdownDiagnostic> _diagnostics = Array.Empty<MarkdownDiagnostic>();
    private long _generation;
    private bool _disposed;

    private MarkdownProgressiveScenePlan(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        IReadOnlyDictionary<string, DeferredSceneWork> works)
    {
        _document = document;
        _session = session;
        _documentOwner = documentOwner;
        _works = works;
    }

    /// <summary>
    /// Builds the immutable descriptor table away from the UI thread. Deferred
    /// callbacks retain their originating extension lifetime from parse time;
    /// scroll-time work only looks up markers on already-laid-out boxes.
    /// </summary>
    internal static Task<MarkdownProgressiveScenePlan?> TryCreateAsync(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        CancellationToken cancellationToken)
        => Task.Run(
            () => cancellationToken.IsCancellationRequested
                ? null
                : TryCreate(document, session, documentOwner, cancellationToken),
            CancellationToken.None);

    internal static MarkdownProgressiveScenePlan? TryCreate(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(documentOwner);
        if (session.IsDisposed || cancellationToken.IsCancellationRequested)
            return null;

        var works = new Dictionary<string, DeferredSceneWork>(StringComparer.Ordinal);
        foreach (KeyValuePair<Block, MarkdownContentFragment> entry in document.ExtensionBlockNodeContent)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Block block = entry.Key;
            if (!MarkdownSyntaxAdapter.TryGetBlockKind(block, out string syntaxKind))
            {
                continue;
            }

            MarkdownSyntaxNode? node = null;
            Collect(entry.Value.Items, content =>
            {
                if (!content.Attributes.TryGetValue(DeferredSceneMarkerAttribute, out string? marker) ||
                    marker is null ||
                    !TryReadMarker(marker, syntaxKind, out _) ||
                    entry.Value.DeferredSceneRenderer is not { } renderer)
                {
                    return;
                }

                MarkdownSyntaxNode blockNode = node ??= MarkdownSyntaxAdapter.CreateBlockNode(block, document.Source);
                if (!content.SourceSpan.Equals(blockNode.SourceSpan))
                    return;

                var work = new DeferredSceneWork(
                    blockNode,
                    renderer,
                    entry.Value.DeferredSceneRendererLifetime,
                    content.Text ?? blockNode.Literal ?? string.Empty,
                    content.Language);
                // A malformed producer must not cause two visible boxes to
                // invoke the same callback/result slot. Independent markers
                // remain distinct even when their SourceSpan values coincide.
                works.TryAdd(marker, work);
            });
        }

        if (cancellationToken.IsCancellationRequested || works.Count == 0)
            return null;

        return new MarkdownProgressiveScenePlan(
            document,
            session,
            documentOwner,
            new ReadOnlyDictionary<string, DeferredSceneWork>(works));
    }

    internal static bool ContainsDeferredSceneFallback(RendererDocument document)
    {
        foreach (MarkdownContentFragment fragment in document.ExtensionBlockNodeContent.Values)
        {
            bool found = false;
            Collect(fragment.Items, content => found = true);
            if (found)
                return true;
        }

        return false;
    }

    internal bool IsFor(RendererDocument document) => ReferenceEquals(_document, document);

    internal bool TryGetResult(string marker, out MarkdownContentFragment? result)
    {
        lock (_gate)
        {
            if (!_disposed && _completed.TryGetValue(marker, out MarkdownDeferredSceneResult? completed))
            {
                result = completed.Fragment;
                return true;
            }
        }

        result = null;
        return false;
    }

    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
                return _diagnostics;
        }
    }

    /// <summary>
    /// Cancels outstanding work at a relayout boundary. Completed scenes are
    /// retained because their vector geometry is independent of the viewport.
    /// </summary>
    internal void AdvanceGeneration()
    {
        CancellationTokenSource oldCancellation;
        Task[] activeTasks;
        lock (_gate)
        {
            if (_disposed)
                return;

            oldCancellation = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
            _generation++;
            activeTasks = _active.Count == 0 ? Array.Empty<Task>() : _active.Values.ToArray();
        }

        _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
            oldCancellation,
            Task.WhenAll(activeTasks));
    }

    internal bool ScheduleVisible(object documentOwner)
    {
#pragma warning disable MR1001 // The optional scheduler integrates with the compatibility control surface.
        if (_session.IsDisposed || documentOwner is not MarkdownRendererControl control)
            return false;
#pragma warning restore MR1001

        LayoutSnapshot? snapshot = control.ProgressiveSceneSnapshot;
        if (snapshot is null || !control.TryGetViewport(out double top, out double height, out _) ||
            !double.IsFinite(top) || !double.IsFinite(height) || height <= 0)
            return true;
        double bottom = top + height;

        if (!snapshot.TryGetMeasuredTopLevelBlocksInBand(
                top,
                bottom,
                out IReadOnlyList<BlockBox> visibleBlocks))
        {
            // The snapshot is being measured by lazy layout. Its completion
            // path invokes this scheduler again for the newly measured band.
            return true;
        }

#pragma warning disable MR1001 // The optional scheduler integrates with the compatibility control surface.
        foreach (CodeBlockBox codeBlock in MarkdownRendererControl.EnumerateCodeBlocks(visibleBlocks))
#pragma warning restore MR1001
        {
            string marker = codeBlock.StableKey;
            if (!_works.ContainsKey(marker) ||
                codeBlock.Bounds.Bottom < top || codeBlock.Bounds.Top > bottom)
            {
                continue;
            }

            TrySchedule(
                marker,
                action => control.DispatcherQueue?.TryEnqueue(() => action()) == true,
                control.RequestRebuildAfterDeferredScene,
                () =>
                {
                    control.DispatcherQueue?.TryEnqueue(control.ScheduleVisibleDeferredScenes);
                });
        }

        return true;
    }

    internal bool TrySchedule(
        string marker,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onGenerationCanceled = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(enqueuePublication);
        ArgumentNullException.ThrowIfNull(onPublished);

        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _session.IsDisposed || !_works.TryGetValue(marker, out DeferredSceneWork? work) ||
                _completed.ContainsKey(marker) || _active.ContainsKey(marker))
                return false;

            generation = _generation;
            token = _generationCancellation.Token;
            Task task = Task.Run(
                () => PrepareAndPublishAsync(
                    marker,
                    work,
                    generation,
                    token,
                    enqueuePublication,
                    onPublished,
                    onGenerationCanceled),
                CancellationToken.None);
            _active.Add(marker, task);
        }

        return true;
    }

    private async Task PrepareAndPublishAsync(
        string marker,
        DeferredSceneWork work,
        long generation,
        CancellationToken generationToken,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onGenerationCanceled)
    {
        MarkdownDeferredSceneResult? result = null;
        try
        {
            generationToken.ThrowIfCancellationRequested();
            using IMarkdownScenePreparationLease sceneLease = await _session
                .EnterScenePreparationAsync(_documentOwner, generationToken)
                .ConfigureAwait(false);
            CancellationToken workToken = sceneLease.CancellationToken;
            workToken.ThrowIfCancellationRequested();
            result = await RenderAsync(work, workToken).ConfigureAwait(false);
            workToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            CompleteWithoutPublication(marker, generation, generationToken, onGenerationCanceled);
            return;
        }
        catch (Exception)
        {
            // A worker/provider failure never escapes into layout or the UI
            // thread. Preserve the original source as accessible code fallback
            // and retain the exact syntax range for the diagnostic.
            result = CreateFailureResult(work, "MDP0001", "Deferred native content could not be prepared.");
        }

        if (result is null || generationToken.IsCancellationRequested)
        {
            CompleteWithoutPublication(marker, generation, generationToken, onGenerationCanceled);
            return;
        }

        bool queued = enqueuePublication(() =>
            PublishOnUiThread(
                marker,
                generation,
                generationToken,
                result,
                onPublished,
                onGenerationCanceled));
        if (!queued)
            CompleteWithoutPublication(marker, generation, generationToken, onGenerationCanceled);
    }

    private void PublishOnUiThread(
        string marker,
        long generation,
        CancellationToken generationToken,
        MarkdownDeferredSceneResult result,
        Action onPublished,
        Action? onGenerationCanceled)
    {
        bool publish;
        bool retryVisible;
        lock (_gate)
        {
            _active.Remove(marker);
            publish = !_disposed &&
                generation == _generation &&
                !generationToken.IsCancellationRequested &&
                !_session.IsDisposed;
            if (publish)
            {
                _completed[marker] = result;
                var diagnostics = new List<MarkdownDiagnostic>();
                foreach (MarkdownDeferredSceneResult completed in _completed.Values)
                    diagnostics.AddRange(completed.Diagnostics);
                diagnostics.Sort(static (left, right) =>
                {
                    int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
                    return comparison != 0
                        ? comparison
                        : string.CompareOrdinal(left.Code, right.Code);
                });
                _diagnostics = diagnostics.Count == 0
                    ? Array.Empty<MarkdownDiagnostic>()
                    : new ReadOnlyCollection<MarkdownDiagnostic>(diagnostics);
            }
            retryVisible = !publish && ShouldRetryVisibleScheduling(generation, generationToken);
        }

        if (publish)
            onPublished();
        else if (retryVisible)
            InvokeRetry(onGenerationCanceled);
    }

    private void CompleteWithoutPublication(
        string marker,
        long generation,
        CancellationToken generationToken,
        Action? onGenerationCanceled)
    {
        bool retryVisible;
        lock (_gate)
        {
            bool removed = _active.Remove(marker);
            retryVisible = removed && ShouldRetryVisibleScheduling(generation, generationToken);
        }
        if (retryVisible)
            InvokeRetry(onGenerationCanceled);
    }

    private bool ShouldRetryVisibleScheduling(long generation, CancellationToken generationToken)
        => !_disposed && !_session.IsDisposed && generation != _generation &&
            generationToken.IsCancellationRequested;

    private static void InvokeRetry(Action? retry)
    {
        try
        {
            retry?.Invoke();
        }
        catch
        {
            // A failed dispatcher retry must not escape from background scene work.
        }
    }

    private static async ValueTask<MarkdownDeferredSceneResult> RenderAsync(
        DeferredSceneWork work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<MarkdownDiagnostic>();
        var context = new MarkdownExtensionContext(work.Node, cancellationToken, diagnostics.Add);
        var content = new MarkdownContentBuilder();
        MarkdownExtensionCallbackLifetime.Lease? producerLease = work.Lifetime?.Enter();
        try
        {
            await work.Renderer(context, content).ConfigureAwait(false);
        }
        finally
        {
            producerLease?.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MarkdownDeferredSceneResult(content.Build(), diagnostics.ToArray());
    }

    private static MarkdownDeferredSceneResult CreateFailureResult(
        DeferredSceneWork work,
        string code,
        string message)
    {
        var fallback = new MarkdownContentBuilder();
        fallback.AddCodeBlock(
            work.FallbackSource,
            work.FallbackLanguage,
            work.Node.SourceSpan,
            Theming.MarkdownStyleRole.CodeBlock);
        return new MarkdownDeferredSceneResult(
            fallback.Build(),
            [new MarkdownDiagnostic(code, MarkdownDiagnosticSeverity.Error, message, work.Node.SourceSpan)]);
    }

    private static void Collect(
        IReadOnlyList<MarkdownContent> content,
        Action<MarkdownContent> onCandidate)
    {
        foreach (MarkdownContent item in content)
        {
            if (item.Kind == MarkdownContentKind.CodeBlock &&
                item.Attributes.ContainsKey(DeferredSceneMarkerAttribute))
            {
                onCandidate(item);
            }

            if (item.Children.Count > 0)
                Collect(item.Children, onCandidate);
        }
    }

    private static bool TryReadMarker(string marker, string syntaxKind, out string token)
    {
        token = string.Empty;
        int separator = marker.IndexOf(MarkerSeparator);
        if (separator <= 0 || separator == marker.Length - 1 ||
            !string.Equals(marker[..separator], syntaxKind, StringComparison.Ordinal))
        {
            return false;
        }

        token = marker[(separator + 1)..];
        return true;
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        Task[] activeTasks;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            cancellation = _generationCancellation;
            activeTasks = _active.Count == 0 ? Array.Empty<Task>() : _active.Values.ToArray();
            _active.Clear();
            _completed.Clear();
            _diagnostics = Array.Empty<MarkdownDiagnostic>();
        }

        _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
            cancellation,
            Task.WhenAll(activeTasks));
    }

    private sealed record DeferredSceneWork(
        MarkdownSyntaxNode Node,
        MarkdownAsyncNodeRenderer Renderer,
        MarkdownExtensionCallbackLifetime? Lifetime,
        string FallbackSource,
        string? FallbackLanguage);
}
