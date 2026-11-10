namespace MarkdownRenderer.Controls.Internal.Recovery;

/// <summary>What the recovery clock needs from the control.</summary>
internal interface IRecoveryClockHost
{
    /// <summary>Retries the deferred work once; called on every tick.</summary>
    void ProcessRecoveryTick();
}