using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance;

/// <summary>
/// The session's parse-and-prepare transaction: parses (progressively when
/// offscreen scenes are deferred), registers the document's progressive
/// scene plan, falls back to an eager reparse when the session is disposed
/// mid-flight, and starts style-role demand collection. Optional stage
/// timings go to <see cref="StageRecorder"/>.
/// </summary>
internal sealed class MarkdownSessionParsePipeline
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly MarkdownDeferredScenePlanRegistry _scenePlans;
    private readonly MarkdownStyleRoleDemandCache _styleRoleDemand;
    private readonly bool _deferOffscreenScenes;

    internal MarkdownSessionParsePipeline(
        MarkdownPerformanceSessionLifetime lifetime,
        MarkdownDeferredScenePlanRegistry scenePlans,
        MarkdownStyleRoleDemandCache styleRoleDemand,
        bool deferOffscreenScenes)
    {
        _lifetime = lifetime;
        _scenePlans = scenePlans;
        _styleRoleDemand = styleRoleDemand;
        _deferOffscreenScenes = deferOffscreenScenes;
    }

    // Hosts set this only while profiling. Payloads contain only an
    // in-process owner identity, fixed stage ID, and stopwatch ticks—never source
    // content or resource identifiers. A typed callback avoids runtime event
    // discovery and remains compatible with NativeAOT trimming.
    internal Action<int, int, long>? StageRecorder { get; set; }

    internal ValueTask<MarkdownDocument?>
        ParseAndPrepareDocumentAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
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

    private async ValueTask<MarkdownDocument?>
        ParseAndPrepareRegisteredDocumentAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
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

    private async ValueTask<MarkdownDocument?> ParseAndPrepareDocumentCoreAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        var timer = new MarkdownParseStageTimer(StageRecorder, documentOwner);
        long sessionStarted = timer.Start();
        timer.Record(MarkdownParsePipelineStage.Begin, startedTimestamp: 0);

        bool parseProgressively = engine is not null && _deferOffscreenScenes && !_lifetime.IsDisposed;
        if (document is null)
        {
            document = await ParseSourceAsync(
                engine,
                source,
                legacyRegistry,
                documentOwner,
                parseProgressively,
                timer,
                cancellationToken).ConfigureAwait(false);
        }

        if (document is null)
        {
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
            timer.Record(MarkdownParsePipelineStage.SessionTotal, sessionStarted);
            return null;
        }

        if (engine is null || !parseProgressively)
        {
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
            if (MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
            {
                document = await ReparseEagerlyAsync(
                    engine,
                    document,
                    legacyRegistry,
                    documentOwner,
                    timer,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            document = await PrepareProgressiveDocumentAsync(
                engine,
                document,
                documentOwner,
                timer,
                cancellationToken).ConfigureAwait(false);
        }

        long roleDemandStarted = timer.Start();
        await _styleRoleDemand.CacheAsync(document, cancellationToken).ConfigureAwait(false);
        timer.Record(MarkdownParsePipelineStage.StyleRoleDemand, roleDemandStarted);
        timer.Record(MarkdownParsePipelineStage.SessionTotal, sessionStarted);
        return document;
    }

    // Parses the source with the engine (progressively when offscreen scenes
    // are deferred) or with the owner's legacy parser. Returns null when
    // neither can parse it.
    private static async ValueTask<MarkdownDocument?> ParseSourceAsync(
        MarkdownEngine? engine,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        bool parseProgressively,
        MarkdownParseStageTimer timer,
        CancellationToken cancellationToken)
    {
        if (engine is not null)
        {
            long engineParseStarted = timer.Start();
            MarkdownDocument parsed = parseProgressively
                ? await engine.ParseForProgressivePresentationAsync(source, cancellationToken).ConfigureAwait(false)
                : await engine.ParseAsync(source, cancellationToken).ConfigureAwait(false);
            timer.Record(MarkdownParsePipelineStage.EngineParseAndCache, engineParseStarted);
            return parsed;
        }

        if (documentOwner is not IMarkdownPerformanceLegacyParser legacyParser)
            return null;

        long legacyParseStarted = timer.Start();
        MarkdownDocument? document = await ParseLegacyDocumentAsync(
            legacyParser,
            source ?? string.Empty,
            legacyRegistry,
            cancellationToken).ConfigureAwait(false);
        timer.Record(MarkdownParsePipelineStage.LegacyParseAndDocument, legacyParseStarted);
        return document;
    }

    // Replaces a document that still holds deferred-scene fallbacks with an
    // eager parse of the same source. Keeps the document when no parser can
    // produce a replacement.
    private static async ValueTask<MarkdownDocument> ReparseEagerlyAsync(
        MarkdownEngine? engine,
        MarkdownDocument document,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        MarkdownParseStageTimer timer,
        CancellationToken cancellationToken)
    {
        if (engine is not null)
        {
            long engineParseStarted = timer.Start();
            MarkdownDocument reparsed = await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);
            timer.Record(MarkdownParsePipelineStage.EngineParseAndCache, engineParseStarted);
            return reparsed;
        }

        if (documentOwner is not IMarkdownPerformanceLegacyParser legacyParser)
            return document;

        long legacyParseStarted = timer.Start();
        MarkdownDocument? legacyDocument = await ParseLegacyDocumentAsync(
            legacyParser,
            document.Source,
            legacyRegistry,
            cancellationToken).ConfigureAwait(false);
        timer.Record(MarkdownParsePipelineStage.LegacyParseAndDocument, legacyParseStarted);
        return legacyDocument ?? document;
    }

    private async ValueTask<MarkdownDocument> PrepareProgressiveDocumentAsync(
        MarkdownEngine engine,
        MarkdownDocument document,
        object documentOwner,
        MarkdownParseStageTimer timer,
        CancellationToken cancellationToken)
    {
        long scenePlanStarted = timer.Start();
        await _scenePlans.PrepareAsync(
            document,
            documentOwner,
            cancellationToken).ConfigureAwait(false);
        timer.Record(MarkdownParsePipelineStage.ProgressiveScenePlan, scenePlanStarted);

        // If the borrowed session is disposed while parse/plan construction is
        // in flight, never leave this control with a deferred fallback and no
        // scheduler. Reparse eagerly; disposal remains safe for concurrent
        // controls because ParseAsync does not depend on the session lifetime.
        if (_lifetime.IsDisposed && MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
        {
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
            long engineParseStarted = timer.Start();
            document = await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);
            timer.Record(MarkdownParsePipelineStage.EngineParseAndCache, engineParseStarted);
        }

        return document;
    }

    private static async ValueTask<MarkdownDocument?> ParseForDisposedSessionAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            if (engine is not null)
                return await engine.ParseAsync(source, cancellationToken).ConfigureAwait(false);

            return documentOwner is IMarkdownPerformanceLegacyParser legacyParser
                ? await ParseLegacyDocumentAsync(
                    legacyParser,
                    source ?? string.Empty,
                    legacyRegistry,
                    cancellationToken).ConfigureAwait(false)
                : null;
        }

        if (!MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
            return document;

        if (engine is not null)
            return await engine.ParseAsync(document.Source, cancellationToken).ConfigureAwait(false);

        if (documentOwner is not IMarkdownPerformanceLegacyParser legacyDocumentParser)
            return document;

        MarkdownDocument? reparsed = await ParseLegacyDocumentAsync(
            legacyDocumentParser,
            document.Source,
            legacyRegistry,
            cancellationToken).ConfigureAwait(false);
        return reparsed ?? document;
    }

    // Runs the owner's legacy parser and builds the immutable document off
    // the caller's thread. Returns null when the legacy parser produced nothing.
    private static async ValueTask<MarkdownDocument?> ParseLegacyDocumentAsync(
        IMarkdownPerformanceLegacyParser legacyParser,
        string source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        CancellationToken cancellationToken)
    {
        MarkdownRenderer.Parsing.ParsedMarkdown? parsed = await legacyParser
            .ParseLegacyMarkdownAsync(source, legacyRegistry, cancellationToken)
            .ConfigureAwait(false);
        if (parsed is null)
            return null;

        return await Task.Run(
            () => MarkdownDocument.FromParsed(
                parsed.SourceText,
                parsed.Document,
                diagnostics: [],
                cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }
}
