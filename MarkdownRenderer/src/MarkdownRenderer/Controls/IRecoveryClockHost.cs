using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls;

/// <summary>What the recovery clock needs from the control.</summary>
internal interface IRecoveryClockHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    DispatcherQueue? DispatcherQueue { get; }

    /// <summary>Retries the deferred work once; called on every tick.</summary>
    void ProcessRecoveryTick();
}
