namespace MarkdownRenderer.Controls.Internal.Input.Links;

internal readonly struct LinkActivationIntent
{
    internal readonly string Url;
    internal readonly string? Title;
    internal readonly SourceSpan SourceSpan;
    internal readonly string? Action;
    internal readonly bool External;
    internal readonly MarkdownLinkInputKind InputKind;
    internal readonly MarkdownInputModifiers Modifiers;
    internal readonly string? DisclosureId;
    internal readonly bool DisclosureExpanded;

    internal LinkActivationIntent(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers)
    {
        Url = link.Url;
        Title = link.Title;
        SourceSpan = link.SourceSpan;
        Action = link.Action;
        External = link.External;
        InputKind = inputKind;
        Modifiers = modifiers;
        if (link.Run is { IsDisclosure: true, DisclosureId: { Length: > 0 } id } run)
        {
            DisclosureId = id;
            DisclosureExpanded = !run.IsExpanded;
        }
        else
        {
            DisclosureId = null;
            DisclosureExpanded = false;
        }
    }
}
