using System.Threading;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// The epoch of the hosted-element focus plan and the generation, layout
/// revision and epoch of the plan last published. Every plan attempt starts
/// a new epoch, which unpublishes the previous plan until the attempt
/// publishes; hosted focus is moved only while a published plan matches the
/// current epoch and layout.
/// </summary>
/// <remarks>
/// Native callbacks can read these from other threads, so every access is
/// volatile.
/// </remarks>
internal sealed class HostedFocusPlanPublication
{
    private long _epoch;
    private long _publishedEpoch = -1;
    private long _publishedGeneration = -1;
    private long _publishedRevision = -1;

    /// <summary>The epoch of the latest plan attempt.</summary>
    internal long Epoch => Volatile.Read(ref _epoch);

    /// <summary>The epoch of the published plan, or -1.</summary>
    internal long PublishedEpoch => Volatile.Read(ref _publishedEpoch);

    /// <summary>The snapshot generation of the published plan, or -1.</summary>
    internal long PublishedGeneration => Volatile.Read(ref _publishedGeneration);

    /// <summary>The layout revision of the published plan, or -1.</summary>
    internal long PublishedRevision => Volatile.Read(ref _publishedRevision);

    /// <summary>Starts a plan attempt, unpublishing the current plan. Returns the new epoch.</summary>
    internal long Invalidate()
    {
        long epoch = unchecked(Volatile.Read(ref _epoch) + 1);
        Volatile.Write(ref _epoch, epoch);
        Volatile.Write(ref _publishedEpoch, -1);
        Volatile.Write(ref _publishedGeneration, -1);
        Volatile.Write(ref _publishedRevision, -1);
        return epoch;
    }

    /// <summary>
    /// Publishes the plan of attempt <paramref name="epoch"/>. The epoch is
    /// written last, so a reader that sees it also sees its generation and
    /// revision.
    /// </summary>
    internal void Publish(long generation, long layoutRevision, long epoch)
    {
        Volatile.Write(ref _publishedGeneration, generation);
        Volatile.Write(ref _publishedRevision, layoutRevision);
        Volatile.Write(ref _publishedEpoch, epoch);
    }
}
