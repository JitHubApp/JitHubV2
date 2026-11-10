namespace JitHub.Markdown.Links;

public readonly record struct MarkdownGitHubRoute(
    MarkdownGitHubRouteKind Kind,
    string? Owner,
    string? Repository,
    int? Number = null)
{
    public static MarkdownGitHubRoute NotInternal =>
        new(MarkdownGitHubRouteKind.NotInternal, null, null);
}
