using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// What keyboard navigation and its geometry retries need from the control
/// when focus geometry cannot be read yet.
/// </summary>
internal interface IFocusGeometryRecoveryHost
{
    /// <summary>Starts geometry recovery when <paramref name="snapshot"/> needs it. Returns true if it does.</summary>
    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>Starts the recovery clock that retries deferred work.</summary>
    void StartRecoveryClock();

    /// <summary>True while the recovery clock is running.</summary>
    bool HasRecoveryClock { get; }

    /// <summary>Rebuilds the layout from the document.</summary>
    void RequestFullRebuild();
}
