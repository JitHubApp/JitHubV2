using System;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Observes Markdown host milestones. Product builds register
/// <see cref="NoMarkdownHostObserver"/>; the lifecycle automation host registers
/// an observer that writes evidence for the out-of-process UI harness.
/// </summary>
internal interface IMarkdownHostObserver
{
    /// <summary>Called when a host has created and named its renderer.</summary>
    /// <param name="automationId">The host's automation id.</param>
    void OnHostReady(string automationId);

    /// <summary>Called when the renderer reports an image it could not load.</summary>
    /// <param name="automationId">The host's automation id.</param>
    /// <param name="source">The image source as written in the document.</param>
    /// <param name="reason">Why the image was unavailable.</param>
    void OnImageUnavailable(string automationId, string source, MarkdownImageUnavailableReason reason);

    /// <summary>Called when the renderer, or its teardown, failed.</summary>
    /// <param name="automationId">The host's automation id.</param>
    /// <param name="exception">The failure.</param>
    void OnRenderFailed(string automationId, Exception exception);

    /// <summary>
    /// Offers a resolved link activation to the observer before it navigates.
    /// </summary>
    /// <param name="automationId">The host's automation id.</param>
    /// <param name="uri">The resolved link target.</param>
    /// <param name="disposition">Where the host would route the link.</param>
    /// <returns><see langword="true"/> when the observer captured the activation and the host must not navigate.</returns>
    bool TryCaptureLinkRoute(string automationId, Uri uri, string disposition);
}
