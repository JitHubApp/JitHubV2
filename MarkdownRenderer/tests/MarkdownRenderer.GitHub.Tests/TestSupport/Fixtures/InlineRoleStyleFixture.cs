using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Theming;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// A theme whose inline roles are each visibly distinct (italic emphasis,
/// bold strong, struck strikethrough, underlined link, mono code with a chip),
/// and a reader for the style the painter uses at one character.
/// </summary>
internal static class InlineRoleStyleFixture
{
    internal const string BodyFamily = "Segoe UI";
    internal const string CodeFamily = "Consolas";
    internal const ushort BoldWeight = 700;
    internal const ushort NormalWeight = 400;
    internal static readonly Color CodeBackground = Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);

    internal static Dictionary<string, ElementStyle> CreateStyles()
    {
        var body = new ElementStyle { FontFamily = BodyFamily, FontSize = 16f };
        return new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.TableCell] = body,
            [MarkdownElementKeys.TableHeader] = body,
            [MarkdownElementKeys.Quote] = body,
            [MarkdownElementKeys.Emphasis] = new ElementStyle
            {
                FontFamily = BodyFamily,
                FontSize = 16f,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
            },
            [MarkdownElementKeys.Strong] = new ElementStyle
            {
                FontFamily = BodyFamily,
                FontSize = 16f,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = BoldWeight },
            },
            [MarkdownElementKeys.Strikethrough] = new ElementStyle
            {
                FontFamily = BodyFamily,
                FontSize = 16f,
                Strikethrough = true,
            },
            [MarkdownElementKeys.Link] = new ElementStyle
            {
                FontFamily = BodyFamily,
                FontSize = 16f,
                Underline = true,
            },
            [MarkdownElementKeys.CodeInline] = new ElementStyle
            {
                FontFamily = CodeFamily,
                FontSize = 13.6f,
                Background = CodeBackground,
                CornerRadius = 6,
            },
        };
    }

    /// <summary>
    /// The style the painter uses for one character: the run's own style, or
    /// the nested span style when the character sits inside a styled link.
    /// </summary>
    internal static ElementStyle ResolveStyleAt(InlineContainerBox box, int index)
    {
        int runStart = 0;
        foreach (InlineRun run in box.Runs)
        {
            int runEnd = runStart + run.Text.Length;
            if (index >= runStart && index < runEnd)
            {
                if (run is LinkRun { InlineStyleMap: { } map } link)
                {
                    ElementStyle[] spanStyles = box.GetLinkInlineSpanStyles(link);
                    int local = index - runStart;
                    for (int spanIndex = 0; spanIndex < map.Count; spanIndex++)
                    {
                        LinkInlineStyleSpan span = map.GetSpan(spanIndex);
                        if (local >= span.Start && local < span.Start + span.Length && span.Path is not null)
                            return spanStyles[spanIndex];
                    }
                }

                return box.GetRunStyle(run);
            }

            runStart = runEnd;
        }

        throw new Xunit.Sdk.XunitException($"No run covers character {index}.");
    }
}
