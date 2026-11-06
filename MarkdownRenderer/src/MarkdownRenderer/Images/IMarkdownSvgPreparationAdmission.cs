namespace MarkdownRenderer.Images;

/// <summary>
/// Optional capability that also bounds the scratch memory an
/// <see cref="IMarkdownSvgRenderer"/> uses in
/// <see cref="IMarkdownSvgRenderer.PrepareSource"/>, from the same capacity as
/// opened documents.
/// </summary>
/// <remarks>
/// The lease covers only the renderer's preparation buffers, not the caller's
/// buffers, caches, XML objects or native resources.
/// </remarks>
public interface IMarkdownSvgPreparationAdmission : IMarkdownSvgAdmission
{
    /// <summary>
    /// Reserves preparation scratch memory without blocking.
    /// </summary>
    /// <param name="demand">The visibility of the image being prepared.</param>
    /// <param name="lease">The reservation, when it succeeds.</param>
    /// <param name="observedCapacityVersion">The <see cref="IMarkdownSvgAdmission.CapacityVersion"/> the decision used.</param>
    /// <param name="requiredFreeBytes">
    /// The full conservative preparation peak, on success and on denial. It is
    /// never a free-capacity difference or an estimate from the input length.
    /// </param>
    /// <returns>True when the reservation succeeded.</returns>
    bool TryReservePreparationScratch(
        MarkdownSvgRenderDemand demand,
        out IMarkdownSvgPreparationLease? lease,
        out long observedCapacityVersion,
        out long requiredFreeBytes);
}
