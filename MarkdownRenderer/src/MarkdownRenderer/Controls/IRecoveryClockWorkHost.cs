using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The deferred UI handoffs the recovery clock retries besides image work:
/// layout publication, lazy layout, overflow-scroll and focus geometry
/// retries and focus plan publication, plus the recovery and rebuild routes
/// a failed tick falls back to.
/// </summary>
internal interface IRecoveryClockWorkHost
{
    /// <summary>True while lazy band realization is queued or running.</summary>
    bool HasPendingLazyLayout { get; }

    /// <summary>True while a horizontal overflow scroll retry is pending.</summary>
    bool HasPendingOverflowScrollRetry { get; }

    /// <summary>True while focus geometry work waits for a free snapshot lease.</summary>
    bool HasPendingFocusGeometryWork { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    /// <summary>Publishes a deferred layout commit, if any.</summary>
    void PublishPendingLayout();

    /// <summary>Retries the latest lazy layout dispatch.</summary>
    void ProcessQueuedLazyLayout();

    /// <summary>Publishes a lazy layout completion whose handoff was lost.</summary>
    void TryPublishLazyLayoutCompletion();

    void ProcessOverflowScrollRetry();

    void ProcessFocusGeometryWork();

    void TryPublishPendingFocusableItems();

    /// <summary>Tells queued pointer input that lazy layout of <paramref name="snapshot"/> failed.</summary>
    void NotifyLazyLayoutFailed(LayoutSnapshot snapshot);
}
