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
                    AppendLiteral(lit, sb, inlineStyles, path);
                    break;
                case CodeInline code:
                    AppendStyledLeaf(
                        code,
                        MarkdownElementKeys.CodeInline,
                        code.Content,
                        sb,
                        ref inlineStyles,
                        path,
                        inlineStyleAliasStart);
                    break;
                case AbbreviationInline abbreviation:
                    AppendStyledLeaf(
                        abbreviation,
                        MarkdownElementKeys.Abbreviation,
                        abbreviation.Abbreviation?.Label ?? string.Empty,
                        sb,
                        ref inlineStyles,
                        path,
                        inlineStyleAliasStart);
                    break;
                case LineBreakInline lineBreak:
                    AppendUnstyled(lineBreak.IsHard ? "\n" : " ", sb, inlineStyles, path);
                    break;
                case EmphasisInline emphasis:
                    FlattenEmphasis(emphasis, sb, ref inlineStyles, path, inlineStyleAliasStart);
                    break;
                case HtmlEntityInline entity:
                    // "Q &amp; A" in link text or alt text keeps its "&".
                    AppendUnstyled(entity.Transcoded.ToString(), sb, inlineStyles, path);
                    break;
                case AutolinkInline autolink:
                    // A flattened run (alt text, a nested label) shows the URL
                    // text; a clickable autolink takes the AddInlines path.
                    AppendUnstyled(autolink.Url, sb, inlineStyles, path);
                    break;
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

    private void AppendLiteral(
        LiteralInline lit,
        System.Text.StringBuilder sb,
        LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path)
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
            AppendUnstyled(lit.Content.ToString(), sb, inlineStyles, path);
        }
    }

    private static void AppendUnstyled(
        string text,
        System.Text.StringBuilder sb,
        LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path)
    {
        int start = sb.Length;
        sb.Append(text);
        inlineStyles?.AddRange(start, sb.Length - start, path);
    }

    // Code and abbreviation leaves get their own style path whenever the
    // caller tracks inline styles or aliases.
    private void AppendStyledLeaf(
        Inline leaf,
        string elementKey,
        string text,
        System.Text.StringBuilder sb,
        ref LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path,
        int inlineStyleAliasStart)
    {
        int start = sb.Length;
        sb.Append(text);
        if (inlineStyles is not null || inlineStyleAliasStart >= 0)
        {
            using var attributes = _context.PushMarkdownAttributes(leaf);
            LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
            LinkInlineStylePath leafPath = builder.CreatePath(
                elementKey,
                _context.Styles.CreateAliasSnapshotFrom(inlineStyleAliasStart),
                path);
            builder.AddRange(start, sb.Length - start, leafPath);
        }
    }

    private void FlattenEmphasis(
        EmphasisInline emphasis,
        System.Text.StringBuilder sb,
        ref LinkInlineStyleMapBuilder? inlineStyles,
        LinkInlineStylePath? path,
        int inlineStyleAliasStart)
    {
        if (inlineStyleAliasStart < 0)
        {
            FlattenContainer(emphasis, sb, ref inlineStyles, path, inlineStyleAliasStart);
            return;
        }

        using var attributes = _context.PushMarkdownAttributes(emphasis);
        LinkInlineStyleMapBuilder builder = inlineStyles ??= new LinkInlineStyleMapBuilder();
        LinkInlineStylePath emphasisPath = builder.CreatePath(
            MarkdownInlineSyntax.GetEmphasisElementKey(emphasis),
            _context.Styles.CreateAliasSnapshotFrom(inlineStyleAliasStart),
            path);
        FlattenContainer(emphasis, sb, ref inlineStyles, emphasisPath, inlineStyleAliasStart);
    }
}
