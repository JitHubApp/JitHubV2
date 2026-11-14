using System.Collections.Concurrent;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;

namespace MarkdownRenderer.Svg.Resvg.Internal.Documents;

/// <summary>
/// The renderer's content-hash quarantine. An identity is added after a
/// worker failure or deadline that the content may have caused; later opens,
/// renders and raster decodes of that identity are refused, and the worker
/// that failed is invalidated.
/// </summary>
internal sealed class SvgContentQuarantine(WorkerPool workers)
{
    private readonly ConcurrentDictionary<string, byte> _identities = new(StringComparer.Ordinal);
    private readonly WorkerPool _workers = workers;

    /// <summary>Whether the identity is quarantined.</summary>
    /// <param name="identity">The content identity.</param>
    /// <returns>True after the identity was added.</returns>
    internal bool Contains(string identity) => _identities.ContainsKey(identity);

    /// <summary>Quarantines the identity without invalidating a worker.</summary>
    /// <param name="identity">The content identity.</param>
    /// <returns>False when it was already quarantined.</returns>
    internal bool Add(string identity) => _identities.TryAdd(identity, 0);

    internal void ThrowIfQuarantined(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_identities.ContainsKey(identity))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "This SVG was quarantined after a worker failure.");
        }
    }

    internal Action CreateContentAdmissionGuard(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return () => ThrowIfQuarantined(identity);
    }

    internal Action CreateContentFailureCallback(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return () => QuarantineWorkerFailure(identity);
    }

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
                _identities.TryAdd(identity, 0);
                _workers.InvalidateWorker(response.WorkerInstanceId);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, response.Detail);
            default:
                _identities.TryAdd(identity, 0);
                _workers.InvalidateWorker(response.WorkerInstanceId);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned an unknown failure.");
        }
    }

    internal void QuarantineWorkerFailure(string identity, long workerInstanceId = 0)
    {
        _identities.TryAdd(identity, 0);
        _workers.InvalidateWorker(workerInstanceId);
    }
}
