using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Optional capability of an <see cref="IMarkdownSvgRenderer"/> that bounds
/// the memory its open documents use. When a renderer implements it, the view
/// opens documents through <see cref="TryOpenAsync"/> and waits for capacity
/// instead of treating a full renderer as a failed image.
/// </summary>
/// <remarks>
/// A capacity denial is temporary and never becomes a content failure. While
/// it waits, the view keeps the image's demand, not its source bytes.
/// </remarks>
public interface IMarkdownSvgAdmission
{
    /// <summary>
    /// Gets an identity shared by every operation that draws on the same
    /// capacity. The view compares it by reference.
    /// </summary>
    object AdmissionDomain { get; }

    /// <summary>
    /// Gets a number that increases whenever capacity is released. A change
    /// alone does not mean enough capacity is free.
    /// </summary>
    long CapacityVersion { get; }

    /// <summary>
    /// Completes when at least <paramref name="requiredFreeBytes"/> may be free
    /// for work of <paramref name="priority"/>. It reserves nothing: the next
    /// <see cref="TryOpenAsync"/> checks capacity again.
    /// </summary>
    /// <param name="requiredFreeBytes">The peak the waiting open needs.</param>
    /// <param name="priority">The priority of the waiting work.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <remarks>
    /// Implementations check free capacity and subscribe in one atomic step,
    /// and must not call back into view code from a lock that releases capacity.
    /// </remarks>
    Task WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a document if capacity allows it, or reports how much it needs.
    /// </summary>
    /// <param name="request">The sanitized source and rendering inputs.</param>
    /// <param name="demand">The visibility of the image, which can rise while the open waits.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>
    /// An opened document, which the caller then owns, or the capacity the
    /// open needs.
    /// </returns>
    ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgRenderDemand demand,
        CancellationToken cancellationToken);
}
