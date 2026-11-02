namespace MarkdownRenderer.Controls.Images;

/// <summary>What image subscriptions need from the control.</summary>
internal interface IImageSubscriptionsHost
{
    /// <summary>Starts the recovery clock that retries deferred work.</summary>
    void StartRecoveryClock();
}
