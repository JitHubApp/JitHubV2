using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Collects each immutable document's style-role demand once, off the caller's
/// thread, keyed weakly by the document. Readers only see completed results.
/// </summary>
internal sealed class MarkdownStyleRoleDemandCache
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly object _styleRoleDemandGate = new();
    private readonly ConditionalWeakTable<MarkdownDocument, Task<MarkdownStyleRoleDemandResult>> _styleRoleDemandCache = new();

    internal MarkdownStyleRoleDemandCache(MarkdownPerformanceSessionLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    internal bool TryGetMask(
        MarkdownDocument document,
        CancellationToken cancellationToken,
        out ulong roleDemandMask)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_lifetime.IsDisposed)
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

    internal async ValueTask CacheAsync(
        MarkdownDocument document,
        CancellationToken cancellationToken)
    {
        if (_lifetime.IsDisposed || cancellationToken.IsCancellationRequested)
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
}
