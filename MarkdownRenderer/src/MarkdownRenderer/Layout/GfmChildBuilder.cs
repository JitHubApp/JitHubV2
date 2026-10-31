using System.Text;
using System.Collections.Generic;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Lightweight block/inline builder for GFM extension renderers. Handles common
/// Markdig block types without requiring access to the internal LayoutBuilder.
/// The GFM, safe-HTML and GitHub packs share this one copy.
/// </summary>
internal static class GfmChildBuilder
{
    /// <summary>Builds child blocks from <paramref name="container"/> and adds them to <paramref name="stack"/>.</summary>
    internal static void PopulateChildren(
        StackBox stack,
        ContainerBlock container,
        MarkdownLayoutContext context,
        SafeHtmlAlignment inheritedAlignment = SafeHtmlAlignment.Inherit)
    {
        // The renderer that owns this stack may throw after child construction
        // begins but before it returns the stack to its caller. Keep that
        // partial owner in the build ledger so already-attached children are
        // still retired on that failure path.
        context.RegisterConstructionBlock(stack);
        SafeHtmlBlockScopeTracker? htmlScopes = context.Registry.SafeHtmlPolicy is null
            ? null
            : context.SafeHtmlNestedScopes?.CloneForNestedContent() ??
              new SafeHtmlBlockScopeTracker(context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = htmlScopes?.BudgetExceeded == true;
        foreach (Block child in container)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            if (alignmentAtBlockEntry == SafeHtmlAlignment.Inherit)
                alignmentAtBlockEntry = inheritedAlignment;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
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
                    context.RegisterConstructionBlock(notice);
                    notice.Add(new TextRun(context.ResolveString(
                        MarkdownStringKeys.HtmlBudgetExceeded,
                        MarkdownLocalizedStrings.HtmlBudgetExceeded))
                    {
                        SourceSpan = MarkdownRenderer.SourceSpan.Empty,
                    });
                    stack.Add(notice);
                    context.AdoptConstructionBlock(notice);
                    htmlBudgetNoticeAdded = true;
                }

                if (child is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }

            if (scopeOnly || suppressedBeforeBlock)
                continue;

            SafeHtmlBlockScopeTracker? nestedScopes = child is ContainerBlock or HtmlBlock
                ? htmlScopes?.CloneForNestedContent()
                : null;
            BlockBox? box;
            using (context.PushSafeHtmlBlockContext(
                       alignmentAtBlockEntry,
                       nestedScopes,
                       inlineFlowScopeIdAtBlockEntry))
                box = TryBuildBlock(child, context);
            if (box is null)
                continue;

            if (child is not HtmlBlock)
                ApplyHtmlAlignment(box, alignmentAtBlockEntry);
            stack.Add(box);
            context.AdoptConstructionBlock(box);

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
                    context.RegisterConstructionBlock(notice);
                    notice.Add(new TextRun(context.ResolveString(
                        MarkdownStringKeys.HtmlBudgetExceeded,
                        MarkdownLocalizedStrings.HtmlBudgetExceeded))
                    {
                        SourceSpan = MarkdownRenderer.SourceSpan.Empty,
                    });
                    stack.Add(notice);
                    context.AdoptConstructionBlock(notice);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }
    }

    private static void ApplyHtmlAlignment(BlockBox box, SafeHtmlAlignment alignment)
    {
        if (box is StackBox { HasResolvedSafeHtmlAlignment: true })
            return;

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
        // A nested list is not a generic stack: it owns marker numbering,
        // tight/loose spacing and list-depth indentation. Use the same builder
        // as top-level lists when called inside a normal layout transaction.
        if (block is ListBlock && context.TryBuildChildBlock(block, out BlockBox? nestedList))
            return nestedList;

        // Code blocks need the owning layout's source projection and chunk
        // planner. Details summaries and other extension-owned containers can
        // contain Markdig code nodes too, so use the same builder as top-level
        // code whenever that owner is available. Keep a shared-layout fallback
        // for internal callers that construct a child outside BuildBlocks.
        if (block is CodeBlock code)
        {
            if (context.TryBuildChildBlock(block, out BlockBox? ownerBuiltCode))
                return ownerBuiltCode;

            return LayoutBuilder.BuildCodeBlockForExtension(code, context);
        }

        using var attrScope = context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        // Check registry first so registered custom renderers are honoured.
        if (context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, context);
            if (custom is not null)
            {
                context.RegisterConstructionBlock(custom);
                box = custom;
            }
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
            ContainerBlock cb => BuildContainer(cb, context),
            _ => null
        };

        if (box is not null)
        {
            context.RegisterConstructionBlock(box);
            if (box.BlockIndex == 0)
                box.BlockIndex = context.NextBlockIndex();
            context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
    }

    private static InlineContainerBox BuildLeaf(LeafBlock leaf, MarkdownLayoutContext context, string elementKey)
    {
        var box = new InlineContainerBox(context, elementKey);
        context.RegisterConstructionBlock(box);
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
        context.RegisterConstructionBlock(stack);
        stack.BlockIndex = context.NextBlockIndex();
        PopulateChildren(stack, cb, context);
        return stack;
    }

    internal static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        System.Func<Inline, bool>? skipFirstIf = null,
        int inheritedAliasStart = -1)
    {
        // A custom built-in renderer can fail while visiting later inline
        // nodes, after earlier image/native runs have already been attached.
        // Register the partially populated owner before entering that walk.
        box.Context.RegisterConstructionBlock(box);
        if (box.Context.TryAppendChildInlines(box, inlines, skipFirstIf, inheritedAliasStart))
            return;
        AddInlines(
            box,
            inlines,
            new SafeHtmlInlineState(box.Context.Registry.SafeHtmlPolicy),
            skipFirstIf,
            inheritedAliasStart,
            System.Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null);
    }

    /// <summary>
    /// Adds a paragraph's inlines from <paramref name="bodySourceStart"/> on,
    /// keeping links, emphasis, code and entities. A GitHub alert uses this
    /// for the text after its <c>[!TAG]</c> marker: inlines that end
    /// before the body are the marker; a literal that straddles it is trimmed.
    /// </summary>
    internal static void AddInlinesFromSource(
        InlineContainerBox box,
        ContainerInline inlines,
        int bodySourceStart)
    {
        box.Context.RegisterConstructionBlock(box);
        AddInlines(
            box,
            inlines,
            new SafeHtmlInlineState(box.Context.Registry.SafeHtmlPolicy),
            skipFirstIf: null,
            inheritedAliasStart: -1,
            System.Array.Empty<string>(),
            containingLinkUrl: null,
            containingLinkTitle: null,
            bodySourceStart);
    }

    private static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        SafeHtmlInlineState htmlState,
        System.Func<Inline, bool>? skipFirstIf,
        int inheritedAliasStart,
        IReadOnlyList<string> inheritedStyleModifiers,
        string? containingLinkUrl,
        string? containingLinkTitle,
        int bodySourceStart = -1)
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
            if (bodySourceStart >= 0)
            {
                if (i.Span.End < bodySourceStart)
                    continue;

                int bodyStart = bodySourceStart;
                bodySourceStart = -1;
                if (i is LiteralInline straddling && straddling.Span.Start < bodyStart)
                {
                    string text = straddling.Content.ToString();
                    int skip = System.Math.Clamp(bodyStart - straddling.Span.Start, 0, text.Length);
                    if (skip < text.Length)
                    {
                        box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                        box.Add(new TextRun(text.Substring(skip))
                        {
                            SourceSpan = new MarkdownRenderer.SourceSpan(bodyStart, straddling.Span.End - bodyStart + 1),
                        });
                    }
                    continue;
                }
            }
            int aliasStart = box.Context.StyleAliasCount;
            using var inlineAttrs = box.Context.PushMarkdownAttributes(i);
            if (i is EmphasisInline emphasis &&
                (box.Context.ThemeSnapshot.HasRelativeScriptFontSizeRules || ContainsLink(emphasis)))
            {
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                string emphasisKey = GetEmphasisElementKey(emphasis);
                using SafeHtmlInlineState.ScriptStyleScope scriptScope =
                    htmlState.PushMarkdownScript(emphasisKey);
                AddInlines(
                    box,
                    emphasis,
                    htmlState,
                    skipFirstIf: null,
                    inheritedAliasStart: effectiveAliasStart,
                    inheritedStyleModifiers: AppendStyleModifier(inheritedStyleModifiers, emphasisKey),
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
                if (run is InlineImageRun image)
                    box.Context.RegisterConstructionBlock(image.Image);
                run.StyleModifierKeys = CombineAliases(
                    run.StyleModifierKeys,
                    inheritedStyleModifiers);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run.SetStyleAliases(CombineAliases(
                    run.StyleAliases,
                    box.Context.CreateStyleAliasSnapshotFrom(effectiveAliasStart)));
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                box.Add(run);
                if (run is InlineImageRun addedImage)
                    box.Context.AdoptConstructionBlock(addedImage.Image);
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
            run is LinkRun or InlineImageRun or InlineSpacerRun ||
            string.IsNullOrWhiteSpace(containingLinkUrl))
        {
            return run;
        }

        var linked = new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
            ScriptStyleAncestryKeys = run.ScriptStyleAncestryKeys,
            SafeHtmlScriptRoleIsRunElement = run.SafeHtmlScriptRoleIsRunElement,
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
            if (child is LinkInline or AutolinkInline)
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

        var run = new InlineImageRun(
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
        context.RegisterConstructionBlock(run.Image);
        return run;
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
                case HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                case AutolinkInline autolink: sb.Append(autolink.Url); break;
                case LinkInline { IsImage: true } image: FlattenInlines(image, sb); break;
                case ContainerInline c2: FlattenInlines(c2, sb); break;
            }
        }
    }
}
