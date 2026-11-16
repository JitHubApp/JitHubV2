using System.Threading;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>
/// The cancellation scope of the loaded view's image work: renewed when the
/// view loads and retired, cancelling its loads, when the view unloads.
/// </summary>
internal sealed class ImageLifetimeScope
{
    private readonly PipelineCancellationRetirement _cancellationRetirement;
    private RetirableCancellationTokenSource? _source;

    internal ImageLifetimeScope(PipelineCancellationRetirement cancellationRetirement)
    {
        _cancellationRetirement = cancellationRetirement;
    }

    /// <summary>The current scope's source; null while the view is unloaded.</summary>
    internal RetirableCancellationTokenSource? Source => _source;

    internal CancellationToken Token => _source?.Token ?? CancellationToken.None;

    /// <summary>Starts a new scope unless the current one is still live.</summary>
    internal void Renew()
    {
        if (_source is null || _source.IsCancellationRequested)
        {
            _source?.Dispose();
            _source = new RetirableCancellationTokenSource();
        }
    }

    internal void Retire()
    {
        var oldImageLifetimeCts = _source;
        _source = null;
        _cancellationRetirement.Retire(oldImageLifetimeCts, lifetime: null);
    }
}
