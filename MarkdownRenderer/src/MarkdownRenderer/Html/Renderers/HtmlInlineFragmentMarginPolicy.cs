namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// How an anonymous inline run between safe-HTML blocks keeps its margin.
/// </summary>
internal enum HtmlInlineFragmentMarginPolicy
{
    PreserveExisting,
    AnonymousContainerFlow,
    Paragraph,
}
