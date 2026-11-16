using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls.Internal.Selection;

internal readonly struct MarkdownTableClipboardPayload
{
    public MarkdownTableClipboardPayload(string Text, string Html)
    {
        this.Text = Text;
        this.Html = Html;
    }

    public readonly string Text;

    public readonly string Html;
}
