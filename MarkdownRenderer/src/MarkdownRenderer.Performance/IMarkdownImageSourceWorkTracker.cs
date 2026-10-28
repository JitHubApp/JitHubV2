using System.Threading.Tasks;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Optional image-source admission capability for tracking resolver work that outlives
/// the resolution call that started it.
/// </summary>
/// <remarks>
/// A resolver may use this capability only while its current resolution call is active.
/// Each task must complete after the detached operation and its cleanup have actually
/// finished; cancellation being requested is not completion. The renderer retains the
/// performance-session lifetime until all registered tasks complete and observes task
/// faults without exposing source text or exception messages. Registration after the
/// resolution call closes is rejected. Do not register process-lifetime or unrelated work.
/// </remarks>
public interface IMarkdownImageSourceWorkTracker
{
    /// <summary>
    /// Extends the performance session's active-work drain until <paramref name="completion"/>
    /// settles, while observing any fault it produces.
    /// </summary>
    /// <param name="completion">
    /// A source-free completion signal for detached work spawned by the current resolution.
    /// It must settle only after all actual work and owned-resource cleanup have completed.
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="completion"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// The resolution call has already returned and closed its work-tracking scope.
    /// </exception>
    void TrackWork(Task completion);
}
