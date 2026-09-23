using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

/// <summary>
/// Renders the bounded static SVG subset in a persistent, kill-on-close resvg worker.
/// </summary>
public sealed class ResvgMarkdownSvgRenderer : IMarkdownSvgRenderer, IDisposable, IAsyncDisposable
{
    /// <inheritdoc />
    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        byte[] prepared = SvgStaticSnapshot.Create(source, cancellationToken);
        SvgResourceBudgetResult budget = SvgResourceBudget.Validate(prepared, cancellationToken);
        if (budget.Accepted)
            return MarkdownSvgSourcePreparation.Admit(prepared);

        string reason = budget.Reason ?? "invalid-content";
        MarkdownSvgFailureReason failureReason = IsResourceLimitReason(reason)
            ? MarkdownSvgFailureReason.ResourceLimitExceeded
            : MarkdownSvgFailureReason.UnsupportedContent;
        return MarkdownSvgSourcePreparation.Reject(
            failureReason,
            $"The SVG was rejected by host preflight ({reason}).");
    }

    private static bool IsResourceLimitReason(string reason) =>
        reason.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("large", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("depth", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("count", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("bytes", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("complexity", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("size", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("length", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("embedded-image-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>The exact upstream resvg release used by the worker.</summary>
    public const string ResvgVersion = "0.48.1";

    /// <summary>The exact upstream source commit used by the worker.</summary>
    public const string ResvgCommit = "68b14c4c3bccdb60344c777406486b54c36ec1a4";

    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerPool _workers;
    private readonly ConcurrentDictionary<string, byte> _quarantine = new(StringComparer.Ordinal);
    private readonly bool _memoryPressureSubscribed;
    private int _disposed;

    /// <inheritdoc />
    public long CacheGeneration => _workers.FontGeneration;

    /// <inheritdoc />
    public event EventHandler? CacheInvalidated;

    internal long SourceAttachmentCount => _workers.SourceAttachmentCount;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Creates a renderer with the immutable default safety ceilings.</summary>
    public ResvgMarkdownSvgRenderer()
        : this(new ResvgMarkdownSvgRendererOptions())
    {
    }

    /// <summary>Creates a renderer with ceilings no greater than the immutable defaults.</summary>
    public ResvgMarkdownSvgRenderer(ResvgMarkdownSvgRendererOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workers = new WorkerPool(options);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (COMException)
        {
            // This WinRT event is unavailable in some unpackaged/test hosts.
            // Such hosts can invoke TrimCachesAsync explicitly.
        }
    }

    /// <summary>Starts and validates the primary worker without parsing an SVG.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _workers.WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (Exception exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Priority))
            throw new ArgumentOutOfRangeException(nameof(request), "The SVG open priority is invalid.");
        if (!Enum.IsDefined(request.ColorScheme))
            throw new ArgumentOutOfRangeException(nameof(request), "The SVG color scheme is invalid.");
        SvgPreflightResult preflight;
        try
        {
            preflight = await SvgPreflight.InspectAsync(request, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }

        string identity = Convert.ToHexString(preflight.Hash);
        if (_quarantine.ContainsKey(identity))
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "This SVG was quarantined after a worker failure.");

        var sourceMemory = new SharedMemoryLease(NewMappingName(), preflight.Source, 0);
        bool transferred = false;
        try
        {
            WorkerOpenResult opened = await _workers.OpenDocumentAsync(
                preflight,
                request,
                sourceMemory,
                cancellationToken).ConfigureAwait(false);
            WorkerResponse response = opened.Response;
            ThrowForRejectedResponse(response, identity);
            if (!double.IsFinite(response.IntrinsicWidth) || response.IntrinsicWidth <= 0 ||
                !double.IsFinite(response.IntrinsicHeight) || response.IntrinsicHeight <= 0 ||
                response.OutputLength != 0 || response.Width != 0 || response.Height != 0)
            {
                _workers.InvalidateWorker(response.WorkerInstanceId);
                _quarantine.TryAdd(identity, 0);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned malformed metadata.");
            }

            var info = new MarkdownSvgDocumentInfo(
                response.IntrinsicWidth,
                response.IntrinsicHeight,
                response.AspectRatio > 0 && double.IsFinite(response.AspectRatio) ? response.AspectRatio : null,
                preflight.Info.Description,
                response.HasText,
                response.UsesCurrentColor,
                response.UsesColorScheme);
            var document = new ResvgMarkdownSvgDocument(
                this,
                _workers,
                _options,
                preflight,
                request,
                identity,
                info,
                opened.Document,
                sourceMemory);
            transferred = true;
            return document;
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerInitializationDeadlineException exception)
        {
            // Initialization is process-specific, not evidence that this SVG is
            // hostile. Never quarantine a content hash for a cold-start timeout.
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (WorkerInitializationException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            _quarantine.TryAdd(identity, 0);
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (Exception exception) when (exception is WorkerProtocolException or IOException or InvalidOperationException)
        {
            _quarantine.TryAdd(identity, 0);
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
        finally
        {
            if (!transferred)
                ((IDisposable)sourceMemory).Dispose();
        }
    }

    /// <summary>
    /// Advances the worker font generation after the host observes <c>WM_FONTCHANGE</c>.
    /// Affected parsed trees are invalidated; source documents remain usable.
    /// </summary>
    public void NotifyFontsChanged()
    {
        ThrowIfDisposed();
        _workers.AdvanceFontGeneration();
        RaiseCacheInvalidated();
    }

    /// <summary>Asks the isolated worker to discard all parsed trees and decoded resources.</summary>
    public async Task TrimCachesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _workers.TrimCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
    }

    /// <summary>Terminates all workers and closes their kill-on-close jobs.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_memoryPressureSubscribed)
            {
                try
                {
                    Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
                }
                catch (COMException)
                {
                }
            }
            _workers.Dispose();
        }
    }

    /// <summary>Terminates all workers and closes their kill-on-close jobs.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal void ThrowForRejectedResponse(WorkerResponse response, string identity)
    {
        switch (response.Status)
        {
            case WorkerStatus.Ok:
                return;
            case WorkerStatus.Unsupported:
                throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent, response.Detail);
            case WorkerStatus.ResourceLimit:
                throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, response.Detail);
            case WorkerStatus.WorkerFailure:
                _workers.InvalidateWorker(response.WorkerInstanceId);
                _quarantine.TryAdd(identity, 0);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, response.Detail);
            default:
                _workers.InvalidateWorker(response.WorkerInstanceId);
                _quarantine.TryAdd(identity, 0);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned an unknown failure.");
        }
    }

    internal void QuarantineWorkerFailure(string identity, long workerInstanceId = 0)
    {
        _workers.InvalidateWorker(workerInstanceId);
        _quarantine.TryAdd(identity, 0);
    }

    private static string NewMappingName() => $"Local\\MarkdownRenderer.Resvg.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private async void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _workers.TrimCacheAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A memory-pressure notification is advisory. WorkerPool still
            // invalidates a failed worker; the event must never escape into WinRT.
        }
    }

    private void RaiseCacheInvalidated()
    {
        Delegate[] handlers = CacheInvalidated?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in handlers)
        {
            try
            {
                var handler = (EventHandler)subscriber;
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // One host cannot prevent other controls from observing the
                // already-published generation change.
            }
        }
    }
}
