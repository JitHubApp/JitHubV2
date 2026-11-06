using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Preparation scratch memory reserved through
/// <see cref="IMarkdownSvgPreparationAdmission.TryReservePreparationScratch"/>.
/// Disposing it releases the reservation.
/// </summary>
public interface IMarkdownSvgPreparationLease : IDisposable
{
    /// <summary>
    /// Shrinks the reservation to the bytes the preparation still holds. A
    /// lease never grows and owns no source bytes.
    /// </summary>
    /// <param name="retainedBytes">The bytes still in use.</param>
    void SetLength(long retainedBytes);
}
