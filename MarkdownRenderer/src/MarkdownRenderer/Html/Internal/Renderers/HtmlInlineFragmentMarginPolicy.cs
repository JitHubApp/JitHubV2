namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// How an anonymous inline run between safe-HTML blocks keeps its margin.
/// </summary>
internal enum HtmlInlineFragmentMarginPolicy
{
    PreserveExisting,
    AnonymousContainerFlow,
    Paragraph,
}
