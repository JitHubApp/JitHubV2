using System;
using JitHub.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// Forwards Markdown host milestones to <see cref="MarkdownLifecycleAutomationBridge"/>
/// so the out-of-process lifecycle harness can read them. Registered only when
/// lifecycle automation is enabled for the process.
/// </summary>
internal sealed class MarkdownLifecycleHostObserver : IMarkdownHostObserver
{
    private readonly MarkdownLifecycleAutomationBridge _bridge;

    /// <param name="bridge">The app's lifecycle automation bridge.</param>
    public MarkdownLifecycleHostObserver(MarkdownLifecycleAutomationBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public void OnHostReady(string automationId) =>
        _bridge.SignalHostReady(automationId);

    public void OnImageUnavailable(string automationId, string source, MarkdownImageUnavailableReason reason) =>
        _bridge.RecordImageUnavailable(automationId, source, reason);

    public void OnRenderFailed(string automationId, Exception exception) =>
        _bridge.RecordRenderFailure(automationId, exception);

    public bool TryCaptureLinkRoute(string automationId, Uri uri, string disposition) =>
        _bridge.RecordLinkRoute(automationId, uri, disposition);
}
