using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Borrowed monotonic demand for queued image-source activation, independent of
/// the image format and of the optional preparation package. The scheduler
/// reconciles current demand before choosing an eviction victim or starter.
/// </summary>
internal interface IMarkdownImageActivationDemand
{
    /// <summary>
    /// Nonblocking, side-effect-free and nonthrowing. A scheduler may read this
    /// monotonic value while holding its queue lock.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// Registers an asynchronous visible-promotion notification, including when
    /// demand is already visible. The callback must never run inline. Disposing
    /// the registration prevents unclaimed callbacks; a callback already claimed
    /// must still validate its queued-operation identity before changing state.
    /// Registration may throw before ownership transfers to the scheduler.
    /// </summary>
    IDisposable RegisterVisiblePromotion(Action callback);
}
