using System;
using System.Text;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a <see cref="QuoteBlock"/> as a styled alert box when its first paragraph
/// starts with a GFM alert tag (<c>[!NOTE]</c>, <c>[!TIP]</c>, etc.).
/// Returns <c>null</c> for plain blockquotes so the core renderer handles them.
/// </summary>
public sealed class AlertRenderer : MarkdownNodeRenderer<QuoteBlock>
{
    private readonly record struct AlertKind(string Tag, string Title, string Icon, Color AccentColor);

    private static readonly AlertKind[] AlertKinds =
    [
        new("[!NOTE]",      "Note",      "\u2139",  Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9)),
        new("[!TIP]",       "Tip",       "\U0001F4A1", Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)),
        new("[!IMPORTANT]", "Important", "\u2757",  Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7)),
        new("[!WARNING]",   "Warning",   "\u26A0",  Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)),
        new("[!CAUTION]",   "Caution",   "\U0001F6AB", Color.FromArgb(0xFF, 0xEF, 0x44, 0x44)),
    ];

    public override BlockBox? BuildBlock(QuoteBlock quoteBlock, MarkdownLayoutContext context)
    {
        // Find first paragraph to detect alert tag
        ParagraphBlock? firstPara = null;
        foreach (var child in quoteBlock)
        {
            if (child is ParagraphBlock p) { firstPara = p; break; }
        }
        if (firstPara?.Inline is null) return null;

        // Flatten first paragraph text for tag detection
        var sb = new StringBuilder();
        GfmChildBuilder.FlattenInlines(firstPara.Inline, sb);
        string firstParaText = sb.ToString().TrimStart();

        AlertKind? alertKind = null;
        foreach (var kind in AlertKinds)
        {
            if (firstParaText.StartsWith(kind.Tag, StringComparison.OrdinalIgnoreCase))
            {
                alertKind = kind;
                break;
            }
        }
        if (alertKind is null) return null;

        var alert = alertKind.Value;

        var stack = new StackBox
        {
            AccentBar = alert.AccentColor,
            ContentPadding = new Thickness(16, 4, 8, 4),
            Margin = new Thickness(0, 4, 0, 4),
        };

        // Title row: icon + alert type name
        var titleBox = new InlineContainerBox(context, MarkdownElementKeys.Strong);
        titleBox.BlockIndex = context.NextBlockIndex();
        titleBox.Add(new StrongRun($"{alert.Icon} {alert.Title}")
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(quoteBlock.Span.Start, 0)
        });
        stack.Add(titleBox);

        // Body content: first child's remaining text (after the tag), then subsequent children
        bool isFirst = true;
        foreach (var child in quoteBlock)
        {
            if (isFirst)
            {
                isFirst = false;
                if (child is ParagraphBlock fp)
                {
                    string remaining = firstParaText.Substring(alert.Tag.Length).TrimStart('\n', '\r', ' ');
                    if (!string.IsNullOrWhiteSpace(remaining))
                    {
                        // Compute a source span that excludes the [!TAG] prefix
                        // so partial selections of the alert body copy the
                        // correct markdown.  Walk forward in the original
                        // source from the paragraph start, skipping the tag
                        // text and any following whitespace.
                        var sourceText = context.SourceMap.SourceText;
                        int bodyStart = fp.Span.Start;
                        int paraEnd = fp.Span.Start + fp.Span.Length;
                        // Find the closing ']' of the [!TAG] marker.
                        int close = -1;
                        if (bodyStart >= 0 && bodyStart < sourceText.Length)
                        {
                            close = sourceText.IndexOf(']', bodyStart, Math.Max(0, paraEnd - bodyStart));
                        }
                        if (close >= 0)
                        {
                            bodyStart = close + 1;
                            while (bodyStart < paraEnd && bodyStart < sourceText.Length
                                && (sourceText[bodyStart] == ' ' || sourceText[bodyStart] == '\t'
                                    || sourceText[bodyStart] == '\n' || sourceText[bodyStart] == '\r'))
                            {
                                bodyStart++;
                            }
                        }
                        int bodyLen = Math.Max(0, paraEnd - bodyStart);

                        var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                        contentBox.BlockIndex = context.NextBlockIndex();
                        contentBox.Add(new TextRun(remaining)
                        {
                            ElementKey = MarkdownElementKeys.Body,
                            SourceSpan = new MarkdownRenderer.SourceSpan(bodyStart, bodyLen)
                        });
                        stack.Add(contentBox);
                    }
                }
                continue;
            }

            var box = GfmChildBuilder.TryBuildBlock(child, context);
            if (box is not null) stack.Add(box);
        }

        return stack;
    }
}
