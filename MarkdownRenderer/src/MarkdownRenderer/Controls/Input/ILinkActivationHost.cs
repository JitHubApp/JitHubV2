using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Input;

/// <summary>What link activation needs from the control.</summary>
internal interface ILinkActivationHost
{
    DispatcherQueue? DispatcherQueue { get; }

    /// <summary>The host's command provider, read anew for every activation.</summary>
    IMarkdownCommandProvider? CommandProvider { get; }

    void SetDisclosureExpanded(LinkRun run, bool expanded);

    void SetDisclosureExpanded(string disclosureId, bool expanded);

    /// <summary>Raises the public LinkClick event.</summary>
    void RaiseLinkClick(MarkdownLinkClickEventArgs args);
}
