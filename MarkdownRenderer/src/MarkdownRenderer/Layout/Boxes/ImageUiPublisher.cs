using System;
using System.Threading;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Runs an image box's publications on the UI dispatcher when one is
/// configured and the caller is off-thread, otherwise inline. Each
/// publication holds one unit of image work from admission until exactly one
/// accepted callback or rejection path releases it.
/// </summary>
/// <remarks>
/// The inline fallback is used by headless hosts and remains guarded by the
/// resource-specific publication and disposal synchronization. If a
/// dispatcher accepts work and silently loses it, the work unit deliberately
/// remains outstanding so the runtime drain timeout can fail closed.
/// </remarks>
internal sealed class ImageUiPublisher
{
    private readonly ImageWorkLifetime _lifetime;
    private readonly MarkdownLayoutContext _context;

    internal ImageUiPublisher(ImageWorkLifetime lifetime, MarkdownLayoutContext context)
    {
        _lifetime = lifetime;
        _context = context;
    }

    /// <summary>
    /// Publishes <paramref name="publish"/>, or runs <paramref name="onDropped"/>
    /// when the box retires first. <paramref name="tryEnqueueOverride"/>
    /// forces the publication through a specific queue.
    /// </summary>
    internal void Publish(
        Action publish,
        Action? onDropped = null,
        Func<Action, bool>? tryEnqueueOverride = null)
    {
        if (!_lifetime.TryBeginWork(out ImageWorkLease? publicationWork) || publicationWork is null)
        {
            new ImageUiPublication(_lifetime, _context, publish, onDropped, publicationWork: null).InvokeDroppedCleanup();
            return;
        }

        var publication = new ImageUiPublication(_lifetime, _context, publish, onDropped, publicationWork);
        var dispatcher = _context.Dispatcher;
        if (tryEnqueueOverride is not null ||
            (dispatcher is not null && !dispatcher.HasThreadAccess))
        {
            bool accepted;
            try
            {
                accepted = tryEnqueueOverride is null
                    ? dispatcher!.TryEnqueue(publication.Run)
                    : tryEnqueueOverride(publication.Run);
            }
            catch
            {
                publication.Reject();
                throw;
            }

            if (!accepted)
                publication.Reject();
        }
        else
        {
            publication.Run();
        }
    }
}
