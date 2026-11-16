using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.GitHubReadmeEngineLayoutFixture;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.InlineRoleStyleFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Building;

/// <summary>
/// Inline code nested in emphasis, strong, strikethrough or a link keeps its
/// code typeface and chip background, as GitHub renders
/// <c>&lt;em&gt;… &lt;code&gt;REPO&lt;/code&gt; …&lt;/em&gt;</c>, while also taking the
/// enclosing role's italic, weight, strikethrough or link treatment.
/// </summary>
public sealed class InlineRunBuilderEmphasisCodeTests
{
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
                Assert.Equal(BoldWeight, style.FontWeight.Weight);
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
}
