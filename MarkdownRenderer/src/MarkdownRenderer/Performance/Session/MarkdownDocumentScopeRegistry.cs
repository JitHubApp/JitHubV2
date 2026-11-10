using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Keeps each control's current document scope, reuses it while the source,
/// extension registry revision, resolver and context are unchanged, and
/// starts opted-in image prefetch once per scope.
/// </summary>
internal sealed class MarkdownDocumentScopeRegistry
{
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly IMarkdownDocumentScopeOpener _opener;
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly bool _prefetchDocumentImages;
    private readonly object _documentScopesGate = new();
    private readonly Dictionary<object, PerformanceDocumentState> _documentScopes =
        new(ReferenceEqualityComparer.Instance);

    internal MarkdownDocumentScopeRegistry(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownDocumentScopeOpener opener,
        MarkdownPerformanceSessionLifetime lifetime,
        bool prefetchDocumentImages)
    {
        _session = session;
        _opener = opener;
        _lifetime = lifetime;
        _prefetchDocumentImages = prefetchDocumentImages;
    }

    internal IMarkdownPerformanceDocumentScope? Prepare(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.Internal.SafeHtml.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        MarkdownPerformanceDocumentScope? retired = null;
        MarkdownPerformanceDocumentScope scope;
        bool startPrefetch = false;
        lock (_documentScopesGate)
        {
            if (_lifetime.IsDisposed)
                return null;

            if (_documentScopes.TryGetValue(documentOwner, out PerformanceDocumentState? current) &&
                string.Equals(current.Source, source, StringComparison.Ordinal) &&
                current.RegistryRevision == registryRevision &&
                current.Scope.Matches(_session, resolver, context))
            {
                scope = current.Scope;
            }
            else
            {
                if (current is not null)
                    retired = current.Scope;
                try
                {
                    scope = _opener.OpenDocument(resolver, context);
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }

                current = new PerformanceDocumentState(source, registryRevision, scope);
                _documentScopes[documentOwner] = current;
            }

            if (_prefetchDocumentImages && !current.PrefetchStarted)
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

    internal void Release(object documentOwner)
    {
        MarkdownPerformanceDocumentScope? scope;
        lock (_documentScopesGate)
        {
            if (!_documentScopes.Remove(documentOwner, out PerformanceDocumentState? state))
                return;
            scope = state.Scope;
        }
        scope.Dispose();
    }

    internal void ReleaseAll()
    {
        MarkdownPerformanceDocumentScope[] scopes;
        lock (_documentScopesGate)
        {
            scopes = _documentScopes.Values.Select(static state => state.Scope).ToArray();
            _documentScopes.Clear();
        }
        foreach (MarkdownPerformanceDocumentScope scope in scopes)
            scope.Dispose();
    }

    private static async Task PrefetchDocumentImagesObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.Internal.SafeHtml.SafeHtmlRenderPolicy? safeHtmlPolicy,
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
}
