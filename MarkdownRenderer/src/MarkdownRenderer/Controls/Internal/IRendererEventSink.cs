using MarkdownRenderer.Images;

namespace MarkdownRenderer.Controls.Internal;

/// <summary>
/// Invokes the control's public events. Only the declaring class can invoke
/// an event, so owners raise them through this sink, which the control
/// implements explicitly. Guards (lifetime, thread, subscriber failures) live
/// in the owners; each member here only invokes its event.
/// </summary>
internal interface IRendererEventSink
{
    void InvokeLinkClick(MarkdownLinkClickEventArgs args);

    void InvokeDisclosureToggled(MarkdownDisclosureToggledEventArgs args);

    void InvokeCopyCompleted(MarkdownCopyCompletedEventArgs args);

    void InvokeImageUnavailable(MarkdownImageUnavailableEventArgs args);

    void InvokeEmbedsRealizationChanged();

    void InvokeRenderCompleted();

    void InvokeRenderFailed(MarkdownRenderFailedEventArgs args);
}
