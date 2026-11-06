using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

internal enum MarkdownTargetCommandDispatchResult
{
    NotProvided,
    Disabled,
    Executed,
    StaleInteraction,
}
