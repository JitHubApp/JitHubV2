using System.Text;
using System.Collections.Generic;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

#if MARKDOWNRENDERER_HTML
namespace MarkdownRenderer.Html.Internal;
#else
namespace MarkdownRenderer.Gfm;
#endif

/// <summary>
/// Lightweight block/inline builder for GFM extension renderers. Handles common
/// Markdig block types without requiring access to the internal LayoutBuilder.
/// </summary>
internal static class GfmChildBuilder
{
    private const int MaxMonolithicTextLayoutLength = 32_768;

    /// <summary>Builds child blocks from <paramref name="container"/> and adds them to <paramref name="stack"/>.</summary>
    internal static void PopulateChildren(StackBox stack, ContainerBlock container, MarkdownLayoutContext context)
    {
        SafeHtmlBlockScopeTracker? htmlScopes = context.Registry.SafeHtmlPolicy is null
            ? null
            : new SafeHtmlBlockScopeTracker(context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = false;
        foreach (Block child in container)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            bool scopeOnly = child is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    context.DisclosureStates,
                    context.CancellationToken) == true;
            if (suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    context.DisclosureStates,
                    context.CancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = context.NextBlockIndex(),
                    };
                    notice.Add(new TextRun(context.ResolveString(
                        MarkdownStringKeys.HtmlBudgetExceeded,
                        MarkdownLocalizedStrings.HtmlBudgetExceeded))
                    {
                        SourceSpan = MarkdownRenderer.SourceSpan.Empty,
                    });
                    stack.Add(notice);
                    htmlBudgetNoticeAdded = true;
                }

                if (child is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }

            if (scopeOnly || suppressedBeforeBlock)
                continue;

            BlockBox? box = TryBuildBlock(child, context);
            if (box is null)
                continue;

            if (htmlScopes is not null)
                ApplyHtmlAlignment(box, htmlScopes.CurrentAlignment);
            stack.Add(box);

            if (!suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    context.DisclosureStates,
                    context.CancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    var notice = new InlineContainerBox(context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = context.NextBlockIndex(),
                    };
                    notice.Add(new TextRun(context.ResolveString(
                        MarkdownStringKeys.HtmlBudgetExceeded,
                        MarkdownLocalizedStrings.HtmlBudgetExceeded))
                    {
                        SourceSpan = MarkdownRenderer.SourceSpan.Empty,
                    });
                    stack.Add(notice);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }
    }

    private static void ApplyHtmlAlignment(BlockBox box, SafeHtmlAlignment alignment)
    {
        if (alignment == SafeHtmlAlignment.Inherit)
            return;

        var canvasAlignment = alignment switch
        {
            SafeHtmlAlignment.Center => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            SafeHtmlAlignment.Right => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
            _ => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
        };
        switch (box)
        {
            case InlineContainerBox inline:
                inline.TextAlignment = canvasAlignment;
                break;
            case ImageBox image:
                image.ContentAlignment = canvasAlignment;
                break;
            case StackBox nested:
                foreach (BlockBox child in nested.Children)
                    ApplyHtmlAlignment(child, alignment);
                break;
        }
    }

    /// <summary>Attempts to build a <see cref="BlockBox"/> for a Markdig block node.</summary>
    internal static BlockBox? TryBuildBlock(Block block, MarkdownLayoutContext context)
    {
        using var attrScope = context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        // Check registry first so registered custom renderers are honoured.
        if (context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, context);
            if (custom is not null)
                box = custom;
        }

        box ??= block switch
        {
            ParagraphBlock p => BuildLeaf(p, context, MarkdownElementKeys.Body),
            HeadingBlock h => BuildLeaf(h, context, h.Level switch
            {
                1 => MarkdownElementKeys.Heading1,
                2 => MarkdownElementKeys.Heading2,
                3 => MarkdownElementKeys.Heading3,
                4 => MarkdownElementKeys.Heading4,
                5 => MarkdownElementKeys.Heading5,
                _ => MarkdownElementKeys.Heading6
            }),
            FencedCodeBlock fenced => BuildCodeBlock(fenced, fenced.Lines.ToString(), context),
            CodeBlock code => BuildCodeBlock(code, code.Lines.ToString(), context),
            ContainerBlock cb => BuildContainer(cb, context),
            _ => null
        };

        if (box is not null)
        {
            if (box.BlockIndex == 0)
                box.BlockIndex = context.NextBlockIndex();
            context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
    }

    private static InlineContainerBox BuildLeaf(LeafBlock leaf, MarkdownLayoutContext context, string elementKey)
    {
        var box = new InlineContainerBox(context, elementKey);
        box.BlockIndex = context.NextBlockIndex();
        if (leaf.Inline is not null)
            AddInlines(box, leaf.Inline);
        return box;
    }

    private static StackBox BuildContainer(ContainerBlock cb, MarkdownLayoutContext context)
    {
        var stack = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        stack.BlockIndex = context.NextBlockIndex();
        PopulateChildren(stack, cb, context);
        return stack;
    }

    private static CodeBlockBox BuildCodeBlock(LeafBlock block, string text, MarkdownLayoutContext context)
    {
        text = CodeBlockMetadata.NormalizeCodeLineEndings(text);
        var metadata = CodeBlockMetadata.FromBlock(block, text);
        int lineCount = CountLogicalLines(text);
        bool showLineNumbers = metadata.ShowLineNumbers ?? context.CodeBlockLineNumberMode switch
        {
            CodeBlockLineNumberMode.Always => true,
            CodeBlockLineNumberMode.Never => false,
            _ => lineCount > 1,
        };
        var codeBox = new CodeBlockBox(
            context,
            metadata,
            text,
            context.IsCodeBlockCopyEnabled,
            showLineNumbers)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        if (text.Length <= MaxMonolithicTextLayoutLength)
        {
            codeBox.AddChunk(BuildCodeBlockChunk(block, metadata, text, 0, text.Length, context));
            return codeBox;
        }

        int offset = 0;
        while (offset < text.Length)
        {
            int length = System.Math.Min(MaxMonolithicTextLayoutLength, text.Length - offset);
            if (offset + length < text.Length)
            {
                int newline = text.LastIndexOf('\n', offset + length - 1, length);
                if (newline > offset)
                    length = newline - offset + 1;
            }

            codeBox.AddChunk(BuildCodeBlockChunk(block, metadata, text.Substring(offset, length), offset, text.Length, context));
            offset += length;
        }

        return codeBox;
    }

    private static InlineContainerBox BuildCodeBlockChunk(
        LeafBlock block,
        CodeBlockMetadata metadata,
        string text,
        int textOffset,
        int totalTextLength,
        MarkdownLayoutContext context)
    {
        var box = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            CodeLanguage = metadata.Language,
            CodeBlockTextOffset = textOffset,
            CodeBlockTextLength = text.Length,
        };
        box.BlockIndex = context.NextBlockIndex();
        box.Add(new TextRun(text)
        {
            SourceSpan = SliceSourceSpan(block, textOffset, text.Length, totalTextLength)
        });
        return box;
    }

    private static int CountLogicalLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int lines = 1;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return text.EndsWith("\n", System.StringComparison.Ordinal) && lines > 1 ? lines - 1 : lines;
    }

    private static MarkdownRenderer.SourceSpan SliceSourceSpan(
        LeafBlock block,
        int textOffset,
        int textLength,
        int totalTextLength)
    {
        if (block.Span.Start < 0 || block.Span.Length <= 0 || totalTextLength <= 0)
            return MarkdownRenderer.SourceSpan.Empty;

        double scale = block.Span.Length / (double)totalTextLength;
        int start = block.Span.Start + (int)System.Math.Round(textOffset * scale);
        int end = block.Span.Start + (int)System.Math.Round((textOffset + textLength) * scale);
        return new MarkdownRenderer.SourceSpan(start, System.Math.Max(0, end - start));
    }

    internal static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        System.Func<Inline, bool>? skipFirstIf = null,
        int inheritedAliasStart = -1)
        => AddInlines(
            box,
            inlines,
            new SafeHtmlInlineState(box.Context.Registry.SafeHtmlPolicy),
            skipFirstIf,
            inheritedAliasStart,
            System.Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null);

    private static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        SafeHtmlInlineState htmlState,
        System.Func<Inline, bool>? skipFirstIf,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        bool skippedFirst = skipFirstIf is null;
        foreach (var i in inlines)
        {
            box.Context.CancellationToken.ThrowIfCancellationRequested();
            if (!skippedFirst)
            {
                skippedFirst = true;
                if (skipFirstIf!(i)) continue;
            }
            int aliasStart = box.Context.StyleAliasCount;
            using var inlineAttrs = box.Context.PushMarkdownAttributes(i);
            if (i is EmphasisInline emphasis && ContainsLink(emphasis))
            {
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    emphasis,
                    htmlState,
                    skipFirstIf: null,
                    inheritedAliasStart: effectiveAliasStart,
                    inheritedStyleModifiers: AppendStyleModifier(
                        inheritedStyleModifiers,
                        GetEmphasisElementKey(emphasis)),
                    containingLinkUrl,
                    containingLinkTitle);
                continue;
            }
            InlineRun? run;
            if (i is HtmlInline html)
            {
                run = htmlState.Process(
                    html,
                    box.Context,
                    containingLinkUrl,
                    containingLinkTitle);
            }
            else if (i is LinkInline link &&
                     TryGetOnlyHtmlImageChild(link, out HtmlInline? linkedHtmlImage) &&
                     htmlState.IsStandaloneImage(linkedHtmlImage, box.Context))
            {
                run = htmlState.Process(
                    linkedHtmlImage,
                    box.Context,
                    link.Url,
                    link.Title,
                    GetLinkedHtmlImageSourceSpan(link, linkedHtmlImage, box.Context));
            }
            else if (i is LinkInline mixedLink &&
                     !mixedLink.IsImage &&
                     ContainsRenderableImage(mixedLink, htmlState, box.Context))
            {
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    mixedLink,
                    htmlState,
                    skipFirstIf: null,
                    inheritedAliasStart: effectiveAliasStart,
                    inheritedStyleModifiers,
                    mixedLink.Url,
                    mixedLink.Title);
                continue;
            }
            else if (!string.IsNullOrWhiteSpace(containingLinkUrl) &&
                     i is LinkInline { IsImage: true } containedImage)
            {
                run = BuildImageRun(
                    containedImage,
                    box.Context,
                    containingLinkUrl,
                    containingLinkTitle,
                    containedImage.Span.Start,
                    containedImage.Span.Length);
            }
            else
            {
                run = htmlState.Apply(BuildInline(i, box.Context));
                run = ApplyContainingLink(run, containingLinkUrl, containingLinkTitle);
            }
            if (run is not null)
            {
                run.StyleModifierKeys = CombineAliases(
                    run.StyleModifierKeys,
                    inheritedStyleModifiers);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run.SetStyleAliases(CombineAliases(
                    run.StyleAliases,
                    box.Context.CreateStyleAliasSnapshotFrom(effectiveAliasStart)));
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                box.Add(run);
            }
            else if (i is ContainerInline nested)
            {
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(
                    box,
                    nested,
                    htmlState,
                    skipFirstIf: null,
                    inheritedAliasStart: effectiveAliasStart,
                    inheritedStyleModifiers: inheritedStyleModifiers,
                    containingLinkUrl,
                    containingLinkTitle);
            }
        }
    }

    private static bool ContainsRenderableImage(
        ContainerInline container,
        SafeHtmlInlineState htmlState,
        MarkdownLayoutContext context)
    {
        foreach (Inline child in container)
        {
            if (child is LinkInline { IsImage: true })
                return true;
            if (child is HtmlInline html && htmlState.IsStandaloneImage(html, context))
                return true;
            if (child is ContainerInline nested && ContainsRenderableImage(nested, htmlState, context))
                return true;
        }

        return false;
    }

    private static InlineRun? ApplyContainingLink(
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            run is LinkRun or InlineImageRun ||
            string.IsNullOrWhiteSpace(containingLinkUrl))
        {
            return run;
        }

        var linked = new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
        };
        linked.SetStyleAliases(run.StyleAliases);
        linked.StyleModifierKeys = string.IsNullOrEmpty(run.ElementKey)
            ? run.StyleModifierKeys
            : AppendStyleModifier(run.StyleModifierKeys, run.ElementKey);
        return linked;
    }

    private static bool ContainsLink(ContainerInline container)
    {
        foreach (Inline child in container)
        {
            if (child is LinkInline)
                return true;
            if (child is ContainerInline nested && ContainsLink(nested))
                return true;
        }

        return false;
    }

    private static string GetEmphasisElementKey(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
            return MarkdownElementKeys.Strikethrough;
        if (emphasis.DelimiterChar == '~')
            return MarkdownElementKeys.Subscript;
        if (emphasis.DelimiterChar == '^')
            return MarkdownElementKeys.Superscript;
        if (emphasis.DelimiterChar == '+')
            return MarkdownElementKeys.Inserted;
        if (emphasis.DelimiterChar == '=')
            return MarkdownElementKeys.Marked;
        return emphasis.DelimiterCount >= 2
            ? MarkdownElementKeys.Strong
            : MarkdownElementKeys.Emphasis;
    }

    private static IReadOnlyList<string> AppendStyleModifier(
        IReadOnlyList<string> modifiers,
        string elementKey)
    {
        var result = new string[modifiers.Count + 1];
        for (int index = 0; index < modifiers.Count; index++)
            result[index] = modifiers[index];
        result[^1] = elementKey;
        return result;
    }

    private static IReadOnlyList<string> CombineAliases(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
            return second;
        if (second.Count == 0)
            return first;

        var result = new string[first.Count + second.Count];
        for (int index = 0; index < first.Count; index++)
            result[index] = first[index];
        for (int index = 0; index < second.Count; index++)
            result[first.Count + index] = second[index];
        return result;
    }

    private static InlineRun? BuildInline(Inline inline, MarkdownLayoutContext context) => inline switch
    {
        LiteralInline lit => new TextRun(lit.Content.ToString())
        {
            // No ElementKey = inherit container's style (fixes headings inside GFM blocks).
            SourceSpan = new MarkdownRenderer.SourceSpan(lit.Span.Start, lit.Span.Length)
        },
        CodeInline ci => new CodeInlineRun(ci.Content)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        },
        EmphasisInline emph => BuildEmphasis(emph),
        LinkInline link => BuildLink(link, context),
        AbbreviationInline abbreviation => new AbbreviationRun(
            abbreviation.Abbreviation?.Label ?? string.Empty,
            abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
        },
        AutolinkInline al => new LinkRun(al.Url, al.Url)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(al.Span.Start, al.Span.Length)
        },
        LineBreakInline lineBreak => new LineBreakRun(lineBreak.IsHard)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(inline.Span.Start, inline.Span.Length)
        },
        HtmlEntityInline entity => new TextRun(entity.Transcoded.ToString())
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(entity.Span.Start, entity.Span.Length)
        },
        _ => null
    };

    private static InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new StringBuilder();
        FlattenInlines(emph, sb);
        var span = new MarkdownRenderer.SourceSpan(emph.Span.Start, emph.Span.Length);
        if (emph.DelimiterChar == '~' && emph.DelimiterCount >= 2)
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '~')
            return new SubscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '^')
            return new SuperscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '+')
            return new InsertedRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '=')
            return new MarkedRun(sb.ToString()) { SourceSpan = span };
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private static TextRun FlattenAsTextRun(ContainerInline ci)
    {
        var sb = new StringBuilder();
        FlattenInlines(ci, sb);
        return new TextRun(sb.ToString())
        {
            // No ElementKey = inherit container's style.
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        };
    }

    private static InlineRun BuildLink(LinkInline link, MarkdownLayoutContext context)
    {
        if (link.IsImage)
        {
            return BuildImageRun(link, context, linkUrl: null, linkTitle: null, link.Span.Start, link.Span.Length);
        }

        if (TryGetOnlyImageChild(link, out var imageLink))
        {
            return BuildImageRun(imageLink, context, link.Url, link.Title, link.Span.Start, link.Span.Length);
        }

        var text = new StringBuilder();
        FlattenInlines(link, text);
        return new LinkRun(text.ToString(), link.Url ?? string.Empty, link.Title)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(link.Span.Start, link.Span.Length)
        };
    }

    private static InlineImageRun BuildImageRun(
        LinkInline imageLink,
        MarkdownLayoutContext context,
        string? linkUrl,
        string? linkTitle,
        int sourceStart,
        int sourceLength)
    {
        var alt = new StringBuilder();
        FlattenInlines(imageLink, alt);
        SafeHtmlLength? requestedWidth = null;
        SafeHtmlLength? requestedHeight = null;
        if (imageLink is SizedImageLinkInline sizedImage)
        {
            requestedWidth = sizedImage.RequestedWidth;
            requestedHeight = sizedImage.RequestedHeight;
        }

        return new InlineImageRun(
            context,
            alt.Length > 0 ? alt.ToString() : "image",
            imageLink.Url ?? string.Empty,
            imageLink.Title,
            linkUrl,
            linkTitle,
            requestedWidth,
            requestedHeight)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(sourceStart, sourceLength)
        };
    }

    private static bool TryGetOnlyImageChild(ContainerInline container, out LinkInline imageLink)
    {
        imageLink = null!;
        int count = 0;
        foreach (var child in container)
        {
            count++;
            if (count > 1)
            {
                imageLink = null!;
                return false;
            }

            if (child is LinkInline { IsImage: true } image)
            {
                imageLink = image;
                continue;
            }

            imageLink = null!;
            return false;
        }

        return imageLink is not null;
    }

    private static bool TryGetOnlyHtmlImageChild(
        ContainerInline container,
        out HtmlInline htmlImage)
    {
        htmlImage = null!;
        int count = 0;
        foreach (Inline child in container)
        {
            if (++count > 1 || child is not HtmlInline html)
            {
                htmlImage = null!;
                return false;
            }

            htmlImage = html;
        }

        return htmlImage is not null;
    }

    private static MarkdownRenderer.SourceSpan GetLinkedHtmlImageSourceSpan(
        LinkInline link,
        HtmlInline htmlImage,
        MarkdownLayoutContext context)
    {
        string source = context.SourceMap.SourceText;
        int tagStart = source.IndexOf(
            htmlImage.Tag,
            System.Math.Clamp(link.Span.Start, 0, source.Length),
            System.StringComparison.Ordinal);
        if (tagStart >= 0)
        {
            int sourceStart = tagStart > 0 && source[tagStart - 1] == '[' ? tagStart - 1 : tagStart;
            int endExclusive = tagStart + htmlImage.Tag.Length;
            if (endExclusive + 1 < source.Length &&
                source[endExclusive] == ']' &&
                source[endExclusive + 1] == '(')
            {
                int closingParenthesis = FindLinkClosingParenthesis(source, endExclusive + 2);
                if (closingParenthesis >= 0)
                    endExclusive = closingParenthesis + 1;
            }

            return new MarkdownRenderer.SourceSpan(sourceStart, endExclusive - sourceStart);
        }

        int start = System.Math.Max(0, System.Math.Min(link.Span.Start, htmlImage.Span.Start));
        int inclusiveEnd = System.Math.Max(link.Span.End, htmlImage.Span.End);
        if (!link.UrlSpan.IsEmpty)
            inclusiveEnd = System.Math.Max(inclusiveEnd, link.UrlSpan.End);

        inclusiveEnd = System.Math.Min(inclusiveEnd, source.Length - 1);
        if (inclusiveEnd + 1 < source.Length && source[inclusiveEnd + 1] == ')')
            inclusiveEnd++;

        return new MarkdownRenderer.SourceSpan(
            start,
            System.Math.Max(0, inclusiveEnd - start + 1));
    }

    private static int FindLinkClosingParenthesis(string source, int destinationStart)
    {
        int depth = 1;
        bool escaped = false;
        for (int index = destinationStart; index < source.Length; index++)
        {
            char current = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    internal static void FlattenInlines(ContainerInline container, StringBuilder sb)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline ci: sb.Append(ci.Content); break;
                case AbbreviationInline ab: sb.Append(ab.Abbreviation?.Label ?? string.Empty); break;
                case LineBreakInline lineBreak: sb.Append(lineBreak.IsHard ? '\n' : ' '); break;
                case LinkInline { IsImage: true } image: FlattenInlines(image, sb); break;
                case ContainerInline c2: FlattenInlines(c2, sb); break;
            }
        }
    }
}
