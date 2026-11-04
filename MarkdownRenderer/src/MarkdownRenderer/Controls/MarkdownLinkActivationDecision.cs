using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

internal readonly struct MarkdownLinkActivationDecision
{
    public MarkdownLinkActivationDecision(
        MarkdownLinkInputKind InputKind,
        MarkdownLinkActivationPolicyResult Result)
    {
        this.InputKind = InputKind;
        this.Result = Result;
    }

    public readonly MarkdownLinkInputKind InputKind;

    public readonly MarkdownLinkActivationPolicyResult Result;

    internal bool IsHandled => Result != MarkdownLinkActivationPolicyResult.RaiseHostEvent;
}
