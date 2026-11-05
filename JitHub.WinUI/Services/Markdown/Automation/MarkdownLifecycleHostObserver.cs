using System;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// Forwards Markdown host milestones to <see cref="MarkdownLifecycleAutomationBridge"/>
/// so the out-of-process lifecycle harness can read them. Registered only when
/// lifecycle automation is enabled for the process.
/// </summary>
internal sealed class MarkdownLifecycleHostObserver : IMarkdownHostObserver
{
    public void OnHostReady(string automationId) =>
        MarkdownLifecycleAutomationBridge.SignalHostReady(automationId);

    public void OnImageUnavailable(string automationId, string source, MarkdownImageUnavailableReason reason) =>
        MarkdownLifecycleAutomationBridge.RecordImageUnavailable(automationId, source, reason);

    public void OnRenderFailed(string automationId, Exception exception) =>
        MarkdownLifecycleAutomationBridge.RecordRenderFailure(automationId, exception);

    public bool TryCaptureLinkRoute(string automationId, Uri uri, string disposition) =>
        MarkdownLifecycleAutomationBridge.RecordLinkRoute(automationId, uri, disposition);
}
