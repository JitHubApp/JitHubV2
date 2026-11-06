namespace MarkdownRenderer.Controls.Selection;

internal readonly struct RenderedSelectionPayload
{
    internal readonly string Text;
    internal readonly string Html;

    internal RenderedSelectionPayload(string text, string html)
    {
        Text = text;
        Html = html;
    }
}
