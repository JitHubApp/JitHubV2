using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class TableIntrinsicWidthTests
{
    private const float ContentWidth = 660f;
    private static readonly FieldInfo LayoutField = typeof(InlineContainerBox).GetField(
        "_layout",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void PrimerLightV5ApiKeyColumnHonorsPaddedInlineCodeIntrinsicMinimum()
    {
        const string source = """
            | API | Description | Auth | HTTPS | CORS |
            |:---|:---|:---|:---|:---|
            | Auth0 | Easy to implement, adaptable authentication and authorization platform | `apiKey` | Yes | Yes |
            | GetOTP | Implement OTP flow quickly | `apiKey` | Yes | No |
            | Micro User Service | User management and authentication | `apiKey` | Yes | No |
            | MojoAuth | Secure and modern passwordless authentication platform | `apiKey` | Yes | Yes |
            | SAWO Labs | Simplify login and improve user experience by integrating passwordless authentication in your app | `apiKey` | Yes | Yes |
            | Stytch | User infrastructure for modern applications | `apiKey` | Yes | No |
            | Warrant | APIs for authorization and access control | `apiKey` | Yes | Yes |
            """;

        using LayoutSnapshot snapshot = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet);
        TableBox table = Assert.Single(snapshot.Blocks.OfType<TableBox>());
        InlineContainerBox[] authCells = table.GetCellInfos()
            .Where(static cell => !cell.IsHeader && cell.Column == 2)
            .Select(static cell => cell.Box)
            .ToArray();
        Assert.Equal(7, authCells.Length);

        foreach (InlineContainerBox cell in authCells)
        {
            CodeInlineRun code = Assert.Single(cell.Runs.OfType<CodeInlineRun>());
            Assert.Equal("apiKey", code.Text);

            IntrinsicWidthMetrics minimum = cell.MeasureIntrinsicWidths(720);
            CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(cell));
            Assert.Single(layout.LineMetrics);
            Assert.True(
                cell.Bounds.Width + 0.01f >= minimum.Minimum,
                $"Auth cell content width {cell.Bounds.Width:R} was below measured intrinsic minimum {minimum.Minimum:R}.");

            Assert.Equal(CanvasHorizontalAlignment.Left, cell.TextAlignment);
        }
    }

    [Fact]
    public void PrimerLightV5CentersOnlyUnalignedHeadersAndKeepsExplicitColumnAlignment()
    {
        const string source = """
            | Left | Center | Right | Default |
            |:---|:---:|---:|---|
            | left body | center body | right body | default body |
            """;

        using LayoutSnapshot v5 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet);
        TableBox v5Table = Assert.Single(v5.Blocks.OfType<TableBox>());
        Assert.Equal(
            [
                CanvasHorizontalAlignment.Left,
                CanvasHorizontalAlignment.Center,
                CanvasHorizontalAlignment.Right,
                CanvasHorizontalAlignment.Center,
            ],
            v5Table.GetCellInfos().Where(static cell => cell.IsHeader).Select(static cell => cell.Box.TextAlignment));
        Assert.Equal(
            [
                CanvasHorizontalAlignment.Left,
                CanvasHorizontalAlignment.Center,
                CanvasHorizontalAlignment.Right,
                CanvasHorizontalAlignment.Left,
            ],
            v5Table.GetCellInfos().Where(static cell => !cell.IsHeader).Select(static cell => cell.Box.TextAlignment));

        using LayoutSnapshot v4 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);
        TableBox v4Table = Assert.Single(v4.Blocks.OfType<TableBox>());
        Assert.Equal(
            [
                CanvasHorizontalAlignment.Left,
                CanvasHorizontalAlignment.Center,
                CanvasHorizontalAlignment.Right,
                CanvasHorizontalAlignment.Left,
            ],
            v4Table.GetCellInfos().Where(static cell => cell.IsHeader).Select(static cell => cell.Box.TextAlignment));

        using LayoutSnapshot v5Rtl = Build(
            source,
            GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet,
            FlowDirection.RightToLeft);
        TableBox v5RtlTable = Assert.Single(v5Rtl.Blocks.OfType<TableBox>());
        Assert.Equal(
            [
                CanvasHorizontalAlignment.Left,
                CanvasHorizontalAlignment.Center,
                CanvasHorizontalAlignment.Right,
                CanvasHorizontalAlignment.Right,
            ],
            v5RtlTable.GetCellInfos().Where(static cell => !cell.IsHeader).Select(static cell => cell.Box.TextAlignment));
        Assert.Equal(
            [
                CanvasHorizontalAlignment.Left,
                CanvasHorizontalAlignment.Center,
                CanvasHorizontalAlignment.Right,
                CanvasHorizontalAlignment.Center,
            ],
            v5RtlTable.GetCellInfos().Where(static cell => cell.IsHeader).Select(static cell => cell.Box.TextAlignment));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight, CanvasHorizontalAlignment.Left)]
    [InlineData(FlowDirection.RightToLeft, CanvasHorizontalAlignment.Right)]
    public void GenericDefaultHeaderAlignmentRemainsDirectional(
        FlowDirection flowDirection,
        CanvasHorizontalAlignment expected)
    {
        const string source = "| Header |\n|---|\n| body |";
        using LayoutSnapshot snapshot = Build(source, MarkdownStyleSheet.Empty, flowDirection);
        TableBox table = Assert.Single(snapshot.Blocks.OfType<TableBox>());

        Assert.Equal(expected, Assert.Single(table.GetCellInfos(), static cell => cell.IsHeader).Box.TextAlignment);
        Assert.Equal(expected, Assert.Single(table.GetCellInfos(), static cell => !cell.IsHeader).Box.TextAlignment);
    }

    private static LayoutSnapshot Build(
        string source,
        MarkdownStyleSheet styleSheet,
        FlowDirection flowDirection = FlowDirection.LeftToRight)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(source, parsed);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(),
            new Dictionary<string, ElementStyleOverride>(),
            GitHubReadmeVisualStyleProfiles.PrimerLightV5.SurfaceColor,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: styleSheet);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            flowDirection);

        return new LayoutBuilder(context, semanticDocument: semanticDocument).Build(parsed, ContentWidth);
    }
}
