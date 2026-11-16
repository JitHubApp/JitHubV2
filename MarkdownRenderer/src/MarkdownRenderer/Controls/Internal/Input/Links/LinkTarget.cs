using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Controls.Internal.Input.Links;

internal readonly struct LinkTarget
{
    internal readonly string Url;
    internal readonly string? Title;
    internal readonly SourceSpan SourceSpan;
    internal readonly LinkRun? Run;
    internal readonly string? Action;
    internal readonly bool External;

    internal LinkTarget(
        string url,
        string? title,
        SourceSpan sourceSpan,
        LinkRun? run,
        string? action = null,
        bool external = false)
    {
        Url = url;
        Title = title;
        SourceSpan = sourceSpan;
        Run = run;
        Action = action;
        External = external;
    }
}
