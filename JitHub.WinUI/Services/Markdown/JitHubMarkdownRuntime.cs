using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using MarkdownRenderer;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace JitHub.Services.Markdown;

/// <summary>
/// Owns the immutable Markdown services shared by every JitHub document surface.
/// The expensive Mermaid and TextMate caches are process-wide and bounded by
/// their renderer defaults instead of being duplicated for every comment or card.
/// </summary>
internal static class JitHubMarkdownRuntime
{
    static JitHubMarkdownRuntime()
    {
        AuthService.AuthenticationCleared += ResetPerformanceSession;
    }

    private static readonly object Gate = new();
    private static MarkdownEngine? _engine;
    private static TextMateCodeBlockSyntaxHighlighter? _codeHighlighter;
    private static ResvgMarkdownSvgRenderer? _svgRenderer;
    private static MarkdownSvgWorkerAuditListener? _svgAuditListener;
    private static MarkdownPerformanceSession? _performanceSession;
    private static long _performanceAccountId = long.MinValue;
    private static Task? _fontChangeTask;
    private static Task? _shutdownTask;
    private static bool _isShuttingDown;

    internal static MarkdownEngine Engine
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
                return _engine ??= new MarkdownEngineBuilder()
                    .UseGitHubReadme()
                    .UseMathematics()
                    .UseMermaid()
                    .Build();
            }
        }
    }

    internal static TextMateCodeBlockSyntaxHighlighter CodeHighlighter
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
                return _codeHighlighter ??= new TextMateCodeBlockSyntaxHighlighter(
                    new CommonTextMateGrammarProvider(),
                    options: null,
                    ownsProvider: true);
            }
        }
    }

    /// <summary>
    /// Gets the process-wide SVG renderer borrowed by markdown controls and the
    /// standalone repository preview. JitHub alone owns its lifetime.
    /// </summary>
    internal static IMarkdownSvgRenderer SvgRenderer
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
                EnsureSvgAuditListenerLocked();
                return _svgRenderer ??= new ResvgMarkdownSvgRenderer();
            }
        }
    }

    /// <summary>Gets one shared opt-in preparation session for the active account.</summary>
    internal static MarkdownPerformanceSession GetPerformanceSession(long accountId)
    {
        MarkdownPerformanceSession? retired = null;
        MarkdownPerformanceSession current;
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
            if (_performanceSession is null || _performanceAccountId != accountId)
            {
                retired = _performanceSession;
                _performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
                _performanceAccountId = accountId;
            }

            current = _performanceSession;
        }

        retired?.Dispose();
        return current;
    }

    /// <summary>Forgets prepared account-scoped bytes as soon as authentication ends.</summary>
    internal static void ResetPerformanceSession()
    {
        MarkdownPerformanceSession? retired;
        lock (Gate)
        {
            retired = _performanceSession;
            _performanceSession = null;
            _performanceAccountId = long.MinValue;
        }

        retired?.Dispose();
    }

    /// <summary>Starts the isolated SVG worker without blocking the shell's first frame.</summary>
    internal static async Task WarmUpSvgRendererAsync(CancellationToken cancellationToken = default)
    {
        ResvgMarkdownSvgRenderer renderer;
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_isShuttingDown, typeof(JitHubMarkdownRuntime));
            EnsureSvgAuditListenerLocked();
            renderer = _svgRenderer ??= new ResvgMarkdownSvgRenderer();
        }

        await renderer.WarmUpAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSvgAuditListenerLocked()
    {
        if (MarkdownLifecycleAutomationBridge.IsEvidenceEnabled)
            _svgAuditListener ??= new MarkdownSvgWorkerAuditListener();
    }

    /// <summary>
    /// Advances the worker font generation without doing process teardown in the
    /// window procedure or starting a renderer that has not otherwise been used.
    /// Repeated font notifications are coalesced while invalidation is pending.
    /// </summary>
    internal static Task NotifyFontsChangedAsync()
    {
        lock (Gate)
        {
            if (_isShuttingDown || _svgRenderer is null)
            {
                return Task.CompletedTask;
            }

            if (_fontChangeTask is { IsCompleted: false })
            {
                return _fontChangeTask;
            }

            ResvgMarkdownSvgRenderer renderer = _svgRenderer;
            return _fontChangeTask = Task.Run(() =>
            {
                lock (Gate)
                {
                    if (!_isShuttingDown && ReferenceEquals(renderer, _svgRenderer))
                    {
                        renderer.NotifyFontsChanged();
                    }
                }
            });
        }
    }

    /// <summary>Stops new work and releases shared native and grammar resources once.</summary>
    internal static Task ShutdownAsync()
    {
        MarkdownEngine? engine;
        TextMateCodeBlockSyntaxHighlighter? codeHighlighter;
        ResvgMarkdownSvgRenderer? svgRenderer;
        MarkdownSvgWorkerAuditListener? svgAuditListener;
        MarkdownPerformanceSession? performanceSession;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _isShuttingDown = true;
            engine = _engine;
            _engine = null;
            codeHighlighter = _codeHighlighter;
            _codeHighlighter = null;
            svgRenderer = _svgRenderer;
            _svgRenderer = null;
            svgAuditListener = _svgAuditListener;
            _svgAuditListener = null;
            performanceSession = _performanceSession;
            _performanceSession = null;
            _performanceAccountId = long.MinValue;
            _fontChangeTask = null;
            _shutdownTask = completion.Task;
        }

        _ = Task.Run(() => ShutdownCoreAsync(
            engine, codeHighlighter, svgRenderer, svgAuditListener, performanceSession, completion));
        return completion.Task;
    }

    /// <summary>
    /// Synchronously tears down any remaining owners during CLR process exit,
    /// where awaiting asynchronous cleanup is no longer reliable.
    /// </summary>
    internal static void ShutdownForProcessExit()
    {
        MarkdownEngine? engine;
        TextMateCodeBlockSyntaxHighlighter? codeHighlighter;
        ResvgMarkdownSvgRenderer? svgRenderer;
        MarkdownSvgWorkerAuditListener? svgAuditListener;
        MarkdownPerformanceSession? performanceSession;
        lock (Gate)
        {
            if (_shutdownTask is not null)
            {
                return;
            }

            _isShuttingDown = true;
            engine = _engine;
            _engine = null;
            codeHighlighter = _codeHighlighter;
            _codeHighlighter = null;
            svgRenderer = _svgRenderer;
            _svgRenderer = null;
            svgAuditListener = _svgAuditListener;
            _svgAuditListener = null;
            performanceSession = _performanceSession;
            _performanceSession = null;
            _performanceAccountId = long.MinValue;
            _fontChangeTask = null;
            _shutdownTask = Task.CompletedTask;
        }

        codeHighlighter?.Dispose();
        performanceSession?.Dispose();
        engine?.Dispose();
        RepositorySvgGpuCache.Shutdown();
        svgRenderer?.Dispose();
        svgAuditListener?.Dispose();
    }

    private static async Task ShutdownCoreAsync(
        MarkdownEngine? engine,
        TextMateCodeBlockSyntaxHighlighter? codeHighlighter,
        ResvgMarkdownSvgRenderer? svgRenderer,
        MarkdownSvgWorkerAuditListener? svgAuditListener,
        MarkdownPerformanceSession? performanceSession,
        TaskCompletionSource completion)
    {
        try
        {
            try
            {
                // Shared owners defer their final provider/native release until admitted
                // callbacks retire, so shutdown does not race active work.
                MarkdownLifecycleAutomationBridge.SignalShutdownStage("highlighter-disposal-started");
                codeHighlighter?.Dispose();
                MarkdownLifecycleAutomationBridge.SignalShutdownStage("performance-session-disposal-started");
                if (performanceSession is not null)
                    await performanceSession.DisposeAsync().ConfigureAwait(false);
                MarkdownLifecycleAutomationBridge.SignalShutdownStage("markdown-engine-disposal-started");
                engine?.Dispose();
                MarkdownLifecycleAutomationBridge.SignalShutdownStage("svg-gpu-cache-shutdown-started");
                RepositorySvgGpuCache.Shutdown();
                MarkdownLifecycleAutomationBridge.SignalShutdownStage("svg-renderer-disposal-started");
                if (svgRenderer is not null)
                    await svgRenderer.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                svgAuditListener?.Dispose();
            }

            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}
