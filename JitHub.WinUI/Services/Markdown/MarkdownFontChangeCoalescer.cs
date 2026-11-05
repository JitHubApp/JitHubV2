using System;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

/// <summary>
/// Coalesces system font-change notifications into one background dispatch.
/// A notification that arrives while a dispatch is still waiting to run joins
/// that dispatch. A notification that arrives once the dispatch has started
/// publishing gets a new dispatch, so no font change is ever lost.
/// </summary>
/// <remarks>
/// The owner's gate protects both the pending dispatch and the state the
/// publish callback reads. Callers hold the gate when they call
/// <see cref="Request"/> and <see cref="Cancel"/>; the dispatch takes the same
/// gate before it publishes.
/// </remarks>
internal sealed class MarkdownFontChangeCoalescer
{
    private readonly object _gate;
    private Task? _pending;

    /// <param name="gate">The owner's lock, shared with the state the publish callback reads.</param>
    internal MarkdownFontChangeCoalescer(object gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>
    /// Returns the dispatch that will publish this notification. The caller holds the gate.
    /// </summary>
    /// <param name="isCurrent">
    /// Checked under the gate when the dispatch runs. When it returns
    /// <see langword="false"/> the owner was shut down or replaced and nothing is published.
    /// </param>
    /// <param name="publish">Publishes the new font generation, under the gate.</param>
    internal Task Request(Func<bool> isCurrent, Action publish)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(publish);
        if (_pending is { IsCompleted: false })
        {
            return _pending;
        }

        return _pending = Task.Run(() =>
        {
            lock (_gate)
            {
                // Only notifications awaiting dispatch are coalesced. A later font
                // change must not join this task after its generation has already
                // been published but before the task runner has marked completion.
                _pending = null;
                if (isCurrent())
                {
                    publish();
                }
            }
        });
    }

    /// <summary>Forgets the pending dispatch so shutdown never waits on it. The caller holds the gate.</summary>
    internal void Cancel() => _pending = null;
}
