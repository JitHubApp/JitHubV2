using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls.Internal.Input.Links;

internal enum MarkdownLinkActivationPolicyResult
{
    InternalTarget,
    DisabledCommand,
    ExecutedCommand,
    RaiseHostEvent,
    StaleInteraction,
}
