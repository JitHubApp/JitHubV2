using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Flattens a Markdig inline container to plain text for alt text, image
/// captions and other text-only consumers.
/// </summary>
internal sealed class InlineContainerFlattener
{
    private readonly MarkdownLayoutContext _context;

    internal InlineContainerFlattener(
        MarkdownLayoutContext context)
    {
        _context = context;
    }

    internal void FlattenContainer(ContainerInline container, System.Text.StringBuilder sb)
    {
        LinkInlineStyleMapBuilder? inlineStyles = null;
        FlattenContainer(container, sb, ref inlineStyles, path: null, inlineStyleAliasStart: -1);
    }

    internal void FlattenContainer(
        ContainerInline container,
        System.Text.StringBuilder sb,
        ref LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path,
        int inlineStyleAliasStart)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit:
                {
                    // A parser literal that is an exact slice of the original
                    // Markdown can be copied into the final wrapper string
                    // directly. Keep the same strict guard as BuildInline so
                    // decoded/transformed/provider-owned content retains the
                    // legacy StringSlice.ToString() behavior.
                    SourceSpan span = new(lit.Span.Start, lit.Span.Length);
                    string? sourceText = lit.Content.Text;
                    if (sourceText is not null &&
                        ReferenceEquals(sourceText, _context.SourceMap.SourceText) &&
                        span.Length > 0 &&
                        span.Start >= 0 &&
                        span.Start <= sourceText.Length - span.Length &&
                        lit.Content.Start == span.Start &&
                        lit.Content.Length == span.Length)
                    {
                        int start = sb.Length;
                        sb.Append(sourceText, span.Start, span.Length);
                        inlineStyles?.AddRange(start, sb.Length - start, path);
                    }
                    else
                    {
                        int start = sb.Length;
                        sb.Append(lit.Content.ToString());
                        inlineStyles?.AddRange(start, sb.Length - start, path);
                    }
                    break;
                }
                case CodeInline code:
                {
                    int start = sb.Length;
                    sb.Append(code.Content);
                    if (inlineStyles is not null || inlineStyleAliasStart >= 0)
                    {
                        using var attributes = _context.PushMarkdownAttributes(code);
                        LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                        LinkInlineStylePath codePath = builder.CreatePath(
                            MarkdownElementKeys.CodeInline,
                            _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                            path);
                        builder.AddRange(start, sb.Length - start, codePath);
                    }
                    break;
                }
                case AbbreviationInline abbreviation:
                {
                    int start = sb.Length;
                    sb.Append(abbreviation.Abbreviation?.Label ?? string.Empty);
                    if (inlineStyles is not null || inlineStyleAliasStart >= 0)
                    {
                        using var attributes = _context.PushMarkdownAttributes(abbreviation);
                        LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                        LinkInlineStylePath abbreviationPath = builder.CreatePath(
                            MarkdownElementKeys.Abbreviation,
                            _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                            path);
                        builder.AddRange(start, sb.Length - start, abbreviationPath);
                    }
                    break;
                }
                case LineBreakInline lineBreak:
                {
                    int start = sb.Length;
                    sb.Append(lineBreak.IsHard ? '\n' : ' ');
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case EmphasisInline emphasis:
                {
                    if (inlineStyleAliasStart < 0)
                    {
                        FlattenContainer(emphasis, sb, ref inlineStyles, path, inlineStyleAliasStart);
                        break;
                    }

                    using var attributes = _context.PushMarkdownAttributes(emphasis);
                    LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
                    LinkInlineStylePath emphasisPath = builder.CreatePath(
                        MarkdownInlineSyntax.GetEmphasisElementKey(emphasis),
                        _context.CreateStyleAliasSnapshotFrom(inlineStyleAliasStart),
                        path);
                    FlattenContainer(emphasis, sb, ref inlineStyles, emphasisPath, inlineStyleAliasStart);
                    break;
                }
                case HtmlEntityInline entity:
                {
                    // "Q &amp; A" in link text or alt text keeps its "&".
                    int start = sb.Length;
                    sb.Append(entity.Transcoded.ToString());
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case AutolinkInline autolink:
                {
                    // A flattened run (alt text, a nested label) shows the URL
                    // text; a clickable autolink takes the AddInlines path.
                    int start = sb.Length;
                    sb.Append(autolink.Url);
                    inlineStyles?.AddRange(start, sb.Length - start, path);
                    break;
                }
                case LinkInline { IsImage: true } image:
                    FlattenContainer(image, sb, ref inlineStyles, path, inlineStyleAliasStart);
                    break;
                case ContainerInline nested:
                    FlattenContainer(nested, sb, ref inlineStyles, path, inlineStyleAliasStart);
                    break;
                default: break;
            }
        }
    }
}
