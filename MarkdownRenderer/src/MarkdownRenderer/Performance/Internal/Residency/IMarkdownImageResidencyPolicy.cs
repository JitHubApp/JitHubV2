using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Performance.Internal.Residency;

/// <summary>
/// Optional session policy that tracks only active and draining ImageBox
/// references. It never owns or terminally disposes boxes.
/// </summary>
internal interface IMarkdownImageResidencyPolicy : IDisposable
{
    /// <summary>
    /// Reconciles the new active viewport images. The optional published list is
    /// supplied only during a plan rebuild, allowing never-visible boxes to be
    /// suspended in that already-linear pass. Scroll reconciliation omits it.
    /// </summary>
    void Reconcile(
        IReadOnlyList<IMarkdownImageResidencyTarget> activeImages,
        IReadOnlyList<IMarkdownImageResidencyTarget>? publishedImages = null);

    /// <summary>Drops borrowed references/subscriptions when the snapshot is retired or replaced.</summary>
    void Reset();
}
