using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.GitHubReadmeEngineLayoutFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Building;

/// <summary>
/// Inline code nested in emphasis, strong, strikethrough or a link keeps its
/// code typeface and chip background, as GitHub renders
/// <c>&lt;em&gt;… &lt;code&gt;REPO&lt;/code&gt; …&lt;/em&gt;</c>, while also taking the
/// enclosing role's italic, weight, strikethrough or link treatment.
/// </summary>
public sealed class InlineRunBuilderEmphasisCodeTests
{
    private const string BodyFamily = "Segoe UI";
    private const string CodeFamily = "Consolas";
    private static readonly Color CodeBackground = Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
    private static readonly Windows.UI.Text.FontWeight Bold = new() { Weight = 700 };

    private const string OhMyZshNote =
        "NOTE: it's incompatible with setting the REPO variable. This setting will take precedence.";

    public static TheoryData<string, string, string, string[]> Cases => new()
    {
        {
            "_NOTE: it's incompatible with setting the `REPO` variable. This setting will take precedence._",
            OhMyZshNote, "REPO", [MarkdownElementKeys.Emphasis]
        },
        {
            // ohmyzsh/ohmyzsh's README, inside a list item.
            "- `REPO` (default: `ohmyzsh/ohmyzsh`): this takes the form of `owner/repository`.\n\n" +
            "  _NOTE: it's incompatible with setting the `REPO` variable. This setting will take precedence._",
            OhMyZshNote, "REPO", [MarkdownElementKeys.Emphasis]
        },
        { "*see `code` here*", "see code here", "code", [MarkdownElementKeys.Emphasis] },
        { "**see `code` here**", "see code here", "code", [MarkdownElementKeys.Strong] },
        { "~~see `code` here~~", "see code here", "code", [MarkdownElementKeys.Strikethrough] },
        { "*`start` of the run*", "start of the run", "start", [MarkdownElementKeys.Emphasis] },
        { "*end of the `run`*", "end of the run", "run", [MarkdownElementKeys.Emphasis] },
        { "_a **b `c`** d_", "a b c d", "c", [MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strong] },
        { "[see `code` here](https://example.test/)", "see code here", "code", [MarkdownElementKeys.Link] },
        {
            "*see [linked `code`](https://example.test/) here*",
            "see linked code here", "code", [MarkdownElementKeys.Emphasis, MarkdownElementKeys.Link]
        },
        { "| head |\n|------|\n| *see `code` here* |", "see code here", "code", [MarkdownElementKeys.Emphasis] },
        { "> [!NOTE]\n> *see `code` here*", "see code here", "code", [MarkdownElementKeys.Emphasis] },
        { "> [!NOTE]\n> [see `code` here](https://example.test/)", "see code here", "code", [MarkdownElementKeys.Link] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CodeInsideEmphasisKeepsCodeTypefaceAndChip(
        string source,
        string paragraphText,
        string codeText,
        string[] enclosingRoles)
    {
        using LayoutSnapshot snapshot = await BuildAsync(source, CreateStyles());

        InlineContainerBox paragraph = Assert.Single(
            EnumerateInlineBoxes(snapshot),
            box => GetText(box) == paragraphText);
        int codeStart = paragraphText.IndexOf(codeText, StringComparison.Ordinal);
        Assert.True(codeStart >= 0);

        ElementStyle code = ResolveStyleAt(paragraph, codeStart);
        Assert.Equal(CodeFamily, code.FontFamily);
        Assert.Equal(CodeBackground, code.Background);
        foreach (string role in enclosingRoles)
            AssertCarriesRole(code, role);

        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(paragraph.TextLayout);
        Assert.Equal(paragraph.LayoutBuffer.Substring(codeStart, codeText.Length), codeText);
        string canvasCodeFamily = CanvasFontFamilyReference.ForCanvas(CodeFamily);
        for (int offset = 0; offset < codeText.Length; offset++)
            Assert.Equal(canvasCodeFamily, layout.GetFontFamily(codeStart + offset));

        // The text around the code keeps the body face and the enclosing role.
        int textIndex = codeStart == 0 ? paragraphText.Length - 1 : 0;
        ElementStyle text = ResolveStyleAt(paragraph, textIndex);
        Assert.Equal(BodyFamily, text.FontFamily);
        Assert.Null(text.Background);
        Assert.Equal(CanvasFontFamilyReference.ForCanvas(BodyFamily), layout.GetFontFamily(textIndex));
    }

    private static void AssertCarriesRole(ElementStyle style, string role)
    {
        switch (role)
        {
            case MarkdownElementKeys.Emphasis:
                Assert.Equal(Windows.UI.Text.FontStyle.Italic, style.FontStyle);
                break;
            case MarkdownElementKeys.Strong:
                Assert.Equal(Bold.Weight, style.FontWeight.Weight);
                break;
            case MarkdownElementKeys.Strikethrough:
                Assert.True(style.Strikethrough);
                break;
            case MarkdownElementKeys.Link:
                Assert.True(style.Underline);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
    }

    // The style the painter uses for one character: the run's own style, or
    // the nested span style when the character sits inside a styled link.
    private static ElementStyle ResolveStyleAt(InlineContainerBox box, int index)
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
                    for (int spanIndex = 0; spanIndex < map.Count; spanIndex++)
                    {
                        LinkInlineStyleSpan span = map.GetSpan(spanIndex);
                        int local = index - runStart;
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

    private static Dictionary<string, ElementStyle> CreateStyles()
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
                FontWeight = Bold,
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
}
