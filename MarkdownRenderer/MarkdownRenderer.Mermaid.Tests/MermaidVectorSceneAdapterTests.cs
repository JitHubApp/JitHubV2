using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidVectorSceneAdapterTests
{
    [Fact]
    public void ConvertPropagatesDescendantBoundsInArbitrarySemanticOrder()
    {
        MermaidScene scene = CreateScene(
            [
                new MermaidSemanticItem(2, MermaidSemanticRole.Group, -1, -1, 2, -1, MermaidSemanticFlags.None),
                new MermaidSemanticItem(3, MermaidSemanticRole.Node, -1, -1, 0, -1, MermaidSemanticFlags.None),
                new MermaidSemanticItem(1, MermaidSemanticRole.Diagram, -1, -1, -1, -1, MermaidSemanticFlags.None),
            ],
            semanticIndex: 1);

        MarkdownVectorScene converted = MermaidVectorSceneAdapter.Convert(
            scene,
            new MarkdownSyntaxNode("fencedCodeBlock", new SourceSpan(0, 3)),
            "abc");

        Assert.Equal(new MarkdownVectorRectangle(10, 20, 30, 40), converted.Semantics[0].Bounds);
        Assert.Equal(new MarkdownVectorRectangle(10, 20, 30, 40), converted.Semantics[1].Bounds);
        Assert.Equal(new MarkdownVectorRectangle(10, 20, 30, 40), converted.Semantics[2].Bounds);
    }

    [Fact]
    public void ConvertObservesCancellationBeforeManagedSceneExpansion()
    {
        MermaidScene scene = CreateScene(
            [new MermaidSemanticItem(1, MermaidSemanticRole.Diagram, -1, -1, -1, -1, MermaidSemanticFlags.None)],
            semanticIndex: 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MermaidVectorSceneAdapter.Convert(
            scene,
            new MarkdownSyntaxNode("fencedCodeBlock", new SourceSpan(0, 3)),
            "abc",
            cancellation.Token));
    }

    [Fact]
    public void ConvertKeepsStrokePathsUnfilledAndReusesTheirImmutableStyle()
    {
        var stroke = new MermaidRgba32(0x12, 0x34, 0x56, 0xff);
        var authoredFill = new MermaidRgba32(0xaa, 0xbb, 0xcc, 0xff);
        MermaidScene scene = new(
            MermaidSceneVersion.Current,
            new MermaidViewport(0, 0, 100, 100),
            [],
            [new MermaidSceneStyle(
                stroke,
                authoredFill,
                2,
                0.75f,
                MermaidStyleFlags.RoundLineCap,
                0,
                0)],
            [0, 0, 25, 10, 50, 0, 0, 20, 25, 30, 50, 20],
            [
                new MermaidDrawCommand(
                    MermaidDrawOpcode.StrokePath,
                    MermaidDrawFlags.None,
                    0,
                    0,
                    -1,
                    0,
                    6,
                    -1,
                    MermaidTextFlags.None),
                new MermaidDrawCommand(
                    MermaidDrawOpcode.StrokePath,
                    MermaidDrawFlags.None,
                    0,
                    0,
                    -1,
                    6,
                    6,
                    -1,
                    MermaidTextFlags.None),
            ],
            [new MermaidSemanticItem(
                1,
                MermaidSemanticRole.Edge,
                -1,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)],
            [],
            [],
            []);

        MarkdownVectorScene converted = MermaidVectorSceneAdapter.Convert(
            scene,
            new MarkdownSyntaxNode("fencedCodeBlock", new SourceSpan(0, 3)),
            "abc");

        MarkdownVectorCommand first = converted.Commands[0];
        MarkdownVectorCommand second = converted.Commands[1];
        Assert.Equal(MarkdownVectorCommandKind.DrawPath, first.Kind);
        Assert.Equal(0x00000000u, first.Style!.FillArgb);
        Assert.Equal(0xFF123456u, first.Style.StrokeArgb);
        Assert.Equal(2, first.Style.StrokeWidth);
        Assert.Equal(0.75f, first.Style.Opacity);
        Assert.Equal(MarkdownVectorLineCap.Round, first.Style.LineCap);
        Assert.Equal(MarkdownVectorPaintRole.Foreground, first.Style.HighContrastFillRole);
        Assert.Equal(MarkdownVectorPaintRole.Foreground, first.Style.HighContrastStrokeRole);
        Assert.Same(first.Style, second.Style);
    }

    [Theory]
    [InlineData("<b>Bold</b>", "Bold")]
    [InlineData("<STRONG class='label'>Strong</STRONG>", "Strong")]
    [InlineData("<span title='a>b'>quoted</span>", "quoted")]
    [InlineData("<i>one</i> and <em>two</em>", "one and two")]
    [InlineData("x<sub>1</sub> + y<sup>2</sup>", "x1 + y2")]
    [InlineData("<custom>literal</custom>", "<custom>literal</custom>")]
    [InlineData("comparison: a < b > c", "comparison: a < b > c")]
    public void NormalizeLabelTextRemovesOnlyKnownFormattingWrappers(string source, string expected)
    {
        Assert.Equal(expected, MermaidVectorSceneAdapter.NormalizeLabelText(source));
    }

    [Fact]
    public void ConvertAndSemanticTextUseTheSameNormalizedLabel()
    {
        var style = new MermaidSceneStyle(
            new MermaidRgba32(0, 0, 0, 0),
            new MermaidRgba32(0, 0, 0, 255),
            0,
            1,
            MermaidStyleFlags.None,
            0,
            0);
        MermaidScene scene = new(
            MermaidSceneVersion.Current,
            new MermaidViewport(0, 0, 100, 100),
            ["<b>Agent</b>", "Segoe UI"],
            [style],
            [10, 20, 30, 40],
            [new MermaidDrawCommand(
                MermaidDrawOpcode.Text,
                MermaidDrawFlags.None,
                0,
                0,
                0,
                0,
                4,
                1,
                MermaidTextFlags.None)],
            [new MermaidSemanticItem(
                1,
                MermaidSemanticRole.Node,
                0,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)],
            [],
            [],
            []);

        MarkdownVectorScene converted = MermaidVectorSceneAdapter.Convert(
            scene,
            new MarkdownSyntaxNode("fencedCodeBlock", new SourceSpan(0, 3)),
            "abc");

        Assert.Equal("Agent", Assert.Single(converted.Commands).Text);
        Assert.Equal("Agent", Assert.Single(converted.Semantics).Name);
        Assert.Equal("Agent", MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    private static MermaidScene CreateScene(
        MermaidSemanticItem[] semantics,
        int semanticIndex)
        => new(
            MermaidSceneVersion.Current,
            new MermaidViewport(0, 0, 100, 100),
            [],
            [],
            [10, 20, 30, 40],
            [new MermaidDrawCommand(
                MermaidDrawOpcode.Rectangle,
                MermaidDrawFlags.None,
                -1,
                semanticIndex,
                -1,
                0,
                4,
                -1,
                MermaidTextFlags.None)],
            semantics,
            [],
            [],
            []);
}
