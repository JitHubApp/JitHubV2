using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown;
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
/// The expensive Mermaid and TextMate caches are shared by the app and bounded by
/// their renderer defaults instead of being duplicated for every comment or card.
/// </summary>
/// <remarks>
/// The app creates one instance and owns its lifetime (see <c>App.MarkdownRuntime</c>).
/// Every provider is created on first use and released once by
/// <see cref="ShutdownAsync"/> or <see cref="ShutdownForProcessExit"/>.
/// </remarks>
internal sealed class JitHubMarkdownRuntime
{
    private readonly object _gate = new();
    private readonly MarkdownFontChangeCoalescer _fontChanges;
    private MarkdownEngine? _engine;
    private TextMateCodeBlockSyntaxHighlighter? _codeHighlighter;
    private ResvgMarkdownSvgRenderer? _svgRenderer;
    private RepositorySvgRasterizer? _svgPreviewRasterizer;
    private MarkdownPerformanceSession? _performanceSession;
    private long _performanceAccountId = long.MinValue;
    private Task? _shutdownTask;
    private bool _isShuttingDown;

    internal JitHubMarkdownRuntime()
    {
        _fontChanges = new MarkdownFontChangeCoalescer(_gate);
    }

    internal MarkdownEngine Engine
    {
        get
        {
            lock (_gate)
            {
                ThrowIfShuttingDown();
                return _engine ??= new MarkdownEngineBuilder()
                    .UseGitHubReadme()
                    .UseMathematics()
                    .UseMermaid()
                    .Build();
            }
        }
    }

    internal TextMateCodeBlockSyntaxHighlighter CodeHighlighter
    {
        get
        {
            lock (_gate)
            {
                ThrowIfShuttingDown();
                return _codeHighlighter ??= new TextMateCodeBlockSyntaxHighlighter(
                    new CommonTextMateGrammarProvider(),
                    options: null,
                    ownsProvider: true);
            }
        }
    }

    /// <summary>
    /// Gets the shared SVG renderer borrowed by markdown controls and the
    /// standalone repository preview. JitHub alone owns its lifetime.
    /// </summary>
    internal IMarkdownSvgRenderer SvgRenderer => GetOrCreateSvgRenderer();

    /// <summary>
    /// Gets the shared static-raster capability of the same explicitly
    /// registered Resvg worker service. This is a separate host registration,
    /// not capability discovery from a control's SVG renderer property.
    /// </summary>
    internal IMarkdownRasterDecoder RasterDecoder => GetOrCreateSvgRenderer();

    /// <summary>
    /// Gets the standalone repository SVG preview's adapter over
    /// <see cref="SvgRenderer"/>. Every preview shares it, so they share one
    /// partition of the tile cache, as they share the renderer.
    /// </summary>
    internal IRepositorySvgRasterizer SvgPreviewRasterizer
    {
        get
        {
            lock (_gate)
            {
                ThrowIfShuttingDown();
                return _svgPreviewRasterizer ??= new RepositorySvgRasterizer(GetOrCreateSvgRenderer());
            }
        }
    }

    /// <summary>Gets one shared opt-in preparation session for the active account.</summary>
    internal MarkdownPerformanceSession GetPerformanceSession(long accountId)
    {
        MarkdownPerformanceSession? retired = null;
        MarkdownPerformanceSession current;
        lock (_gate)
        {
            ThrowIfShuttingDown();
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
    internal void ResetPerformanceSession()
    {
        MarkdownPerformanceSession? retired;
        lock (_gate)
        {
            retired = _performanceSession;
            _performanceSession = null;
            _performanceAccountId = long.MinValue;
        }

        retired?.Dispose();
    }

    /// <summary>Starts the isolated SVG worker without blocking the shell's first frame.</summary>
    internal async Task WarmUpSvgRendererAsync(CancellationToken cancellationToken = default)
    {
        ResvgMarkdownSvgRenderer renderer = GetOrCreateSvgRenderer();
        await renderer.WarmUpAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances the worker font generation without doing process teardown in the
    /// window procedure or starting a renderer that has not otherwise been used.
    /// Repeated font notifications are coalesced while invalidation is pending.
    /// </summary>
    internal Task NotifyFontsChangedAsync()
    {
        lock (_gate)
        {
            if (_isShuttingDown || _svgRenderer is not { } renderer)
            {
                return Task.CompletedTask;
            }

            return _fontChanges.Request(
                () => !_isShuttingDown && ReferenceEquals(renderer, _svgRenderer),
                renderer.NotifyFontsChanged);
        }
    }

    /// <summary>Stops new work and releases shared native and grammar resources once.</summary>
    internal Task ShutdownAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RetiredProviders retired;
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            retired = RetireProviders(completion.Task);
        }

        _ = Task.Run(() => ShutdownCoreAsync(retired, completion));
        return completion.Task;
    }

    /// <summary>
    /// Synchronously tears down any remaining owners during CLR process exit,
    /// where awaiting asynchronous cleanup is no longer reliable.
    /// </summary>
    internal void ShutdownForProcessExit()
    {
        RetiredProviders retired;
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return;
            }

            retired = RetireProviders(Task.CompletedTask);
        }

        retired.CodeHighlighter?.Dispose();
        retired.PerformanceSession?.Dispose();
        retired.Engine?.Dispose();
        RepositorySvgGpuCache.Shutdown();
        retired.SvgRenderer?.Dispose();
    }

    private ResvgMarkdownSvgRenderer GetOrCreateSvgRenderer()
    {
        lock (_gate)
        {
            ThrowIfShuttingDown();
            return _svgRenderer ??= new ResvgMarkdownSvgRenderer();
        }
    }

    private void ThrowIfShuttingDown() => ObjectDisposedException.ThrowIf(_isShuttingDown, this);

    /// <summary>Marks the runtime as shutting down and takes its providers. The caller holds the gate.</summary>
    private RetiredProviders RetireProviders(Task shutdownTask)
    {
        _isShuttingDown = true;
        RetiredProviders retired = new(_engine, _codeHighlighter, _svgRenderer, _performanceSession);
        _engine = null;
        _codeHighlighter = null;
        _svgRenderer = null;
        _svgPreviewRasterizer = null;
        _performanceSession = null;
        _performanceAccountId = long.MinValue;
        _fontChanges.Cancel();
        _shutdownTask = shutdownTask;
        return retired;
    }

    private static async Task ShutdownCoreAsync(RetiredProviders retired, TaskCompletionSource completion)
    {
        try
        {
            // Shared owners defer their final provider/native release until admitted
            // callbacks retire, so shutdown does not race active work.
            retired.CodeHighlighter?.Dispose();
            if (retired.PerformanceSession is not null)
                await retired.PerformanceSession.DisposeAsync().ConfigureAwait(false);
            retired.Engine?.Dispose();
            RepositorySvgGpuCache.Shutdown();
            if (retired.SvgRenderer is not null)
                await retired.SvgRenderer.DisposeAsync().ConfigureAwait(false);

            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private readonly record struct RetiredProviders(
        MarkdownEngine? Engine,
        TextMateCodeBlockSyntaxHighlighter? CodeHighlighter,
        ResvgMarkdownSvgRenderer? SvgRenderer,
        MarkdownPerformanceSession? PerformanceSession);
}
