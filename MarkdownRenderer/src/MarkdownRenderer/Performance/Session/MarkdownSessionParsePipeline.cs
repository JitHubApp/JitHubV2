using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

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

    internal ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        ParseAndPrepareDocumentAsync(
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
        Action<int, int, long>? parseStageRecorder = StageRecorder;
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

        bool parseProgressively = engine is not null && _deferOffscreenScenes && !_lifetime.IsDisposed;
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
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
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
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
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
            await _styleRoleDemand.CacheAsync(document, cancellationToken).ConfigureAwait(false);
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
        await _scenePlans.PrepareAsync(
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
        if (_lifetime.IsDisposed && MarkdownProgressiveScenePlan.ContainsDeferredSceneFallback(document))
        {
            await _scenePlans.Release(documentOwner).ConfigureAwait(false);
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
        await _styleRoleDemand.CacheAsync(document, cancellationToken).ConfigureAwait(false);
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
}
