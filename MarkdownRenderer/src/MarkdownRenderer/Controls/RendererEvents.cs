using System;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Raises the control's public events with their guards: no event after
/// disposal where the control promised none, image events marshalled to the
/// UI thread, and subscriber failures kept from breaking the pipeline.
/// </summary>
internal sealed class RendererEvents
{
    private readonly IRendererEventSink _sink;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly DispatcherQueue? _dispatcher;

    internal RendererEvents(IRendererEventSink sink, MarkdownViewLifetime lifetime, DispatcherQueue? dispatcher)
    {
        _sink = sink;
        _lifetime = lifetime;
        _dispatcher = dispatcher;
    }

    internal void RaiseLinkClick(MarkdownLinkClickEventArgs args) => _sink.InvokeLinkClick(args);

    internal void RaiseDisclosureToggled(bool expanded) =>
        _sink.InvokeDisclosureToggled(new MarkdownDisclosureToggledEventArgs(expanded));

    internal void RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason)
    {
        if (_lifetime.IsDisposed || reason == MarkdownImageUnavailableReason.None)
            return;

        void Raise()
        {
            if (!_lifetime.IsDisposed)
                _sink.InvokeImageUnavailable(new MarkdownImageUnavailableEventArgs(source, reason, svgFailureReason));
        }

        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }

    internal void RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
    {
        try
        {
            _sink.InvokeCopyCompleted(new MarkdownCopyCompletedEventArgs(kind, succeeded));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Copy completion subscriber failed: {ex.Message}");
        }
    }

    internal void RaiseEmbedsRealizationChanged()
    {
        try { _sink.InvokeEmbedsRealizationChanged(); }
        catch { /* subscriber threw — swallow to keep scroll pipeline alive. */ }
    }
}
