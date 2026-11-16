using System;

namespace MarkdownRenderer.Performance.Internal.Residency;

/// <summary>
/// An ImageBox-facing lifecycle seam implemented by the base renderer. Residency
/// policy callbacks run only while the owning snapshot's read lease is held;
/// the drain event is a wake-up signal and must not mutate the target itself.
/// </summary>
internal interface IMarkdownImageResidencyTarget
{
    event EventHandler? ResidencyWorkDrained;

    void SuspendResources();

    bool TryReleaseSuspendedResources();

    bool TryResumeResources();
}
