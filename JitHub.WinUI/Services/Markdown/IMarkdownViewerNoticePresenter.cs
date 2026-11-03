namespace JitHub.Services.Markdown;

/// <summary>
/// What <see cref="MarkdownViewerFeedback"/> needs from the Markdown host that
/// shows its notices. Notices overlay the reading region; presenting one never
/// resizes it.
/// </summary>
internal interface IMarkdownViewerNoticePresenter
{
    /// <summary>Gets the host kind, which selects compact notice titles.</summary>
    string? HostKind { get; }

    /// <summary>Gets the host's renderer automation id.</summary>
    string AutomationId { get; }

    /// <summary>Gets whether the host loads secure third-party images without per-document consent.</summary>
    bool AllowsThirdPartyRemoteImagesByDefault { get; }

    /// <summary>Gets whether the host's renderer teardown failed; its failure notice then stays up.</summary>
    bool IsRendererDisposalFaulted { get; }

    /// <summary>Updates the remote-image notice content without changing whether it is open.</summary>
    /// <param name="title">The notice title.</param>
    /// <param name="message">The notice message, or an empty string for compact notices.</param>
    /// <param name="offerLoadImages">Whether the notice offers to load the blocked images.</param>
    void UpdateRemoteImageNotice(string title, string message, bool offerLoadImages);

    /// <summary>Opens the remote-image notice.</summary>
    void OpenRemoteImageNotice();

    /// <summary>Closes the remote-image notice when it is present.</summary>
    void CloseRemoteImageNotice();

    /// <summary>Opens or closes the render-error notice when it is present.</summary>
    /// <param name="isOpen">Whether the notice is open.</param>
    void SetRenderErrorNoticeOpen(bool isOpen);

    /// <summary>Lets the current renderer load third-party images and rebuild.</summary>
    void ApplyRemoteImageGrant();

    /// <summary>Asks the current renderer to rebuild its document.</summary>
    void RequestRebuild();
}
