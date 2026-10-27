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
    [InlineData("<b>Agent</b>")]
    [InlineData("<STRONG class='label'>Strong</STRONG>")]
    [InlineData("<span title='a>b'>quoted</span>")]
    [InlineData("<i>one</i> and <em>two</em>")]
    [InlineData("x<sub>1</sub> + y<sup>2</sup>")]
    [InlineData("<custom>literal</custom>")]
    [InlineData("comparison: a < b > c")]
    [InlineData("&quot;")]
    [InlineData("&amp;quot;")]
    public void ConvertAndSemanticTextPreserveTheSameFinalLiteralLabel(string text)
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
            [text, "Segoe UI"],
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

        // MMIR carries final literal display text, not source markup. Raw
        // formatting classification belongs before native text measurement;
        // repeating it here destroys correctly decoded angle-bracket labels.
        Assert.Equal(text, Assert.Single(converted.Commands).Text);
        Assert.Equal(text, Assert.Single(converted.Semantics).Name);
        Assert.Equal(text, MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextSuppressesOwnedShapingRunsForAnExplicitSemanticName()
    {
        const string label = "مرحبا API 👩‍💻";
        MermaidScene scene = CreateTextScene(
            ["مرحبا", " API", " 👩‍💻", label],
            [
                CreateTextCommand(0, semanticIndex: 0),
                CreateTextCommand(1, semanticIndex: 0),
                CreateTextCommand(2, semanticIndex: 0),
            ],
            [new MermaidSemanticItem(
                10,
                MermaidSemanticRole.Node,
                NameIndex: 3,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)]);

        Assert.Equal(label, MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextPreservesRepeatedOccurrencesBySemanticIdentity()
    {
        const string label = "Same label";
        MermaidScene scene = CreateTextScene(
            [label],
            [
                CreateTextCommand(0, semanticIndex: 0),
                CreateTextCommand(0, semanticIndex: 1),
            ],
            [
                new MermaidSemanticItem(
                    10,
                    MermaidSemanticRole.Node,
                    NameIndex: 0,
                    -1,
                    -1,
                    -1,
                    MermaidSemanticFlags.None),
                new MermaidSemanticItem(
                    11,
                    MermaidSemanticRole.Node,
                    NameIndex: 0,
                    -1,
                    -1,
                    -1,
                    MermaidSemanticFlags.None),
            ]);

        Assert.Equal($"{label}{Environment.NewLine}{label}", MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextPreservesUnnamedOwnedRunsAndUnownedText()
    {
        MermaidScene scene = CreateTextScene(
            ["repeat", "repeat", "outside"],
            [
                CreateTextCommand(0, semanticIndex: 0),
                CreateTextCommand(1, semanticIndex: 0),
                CreateTextCommand(2, semanticIndex: -1),
            ],
            [new MermaidSemanticItem(
                10,
                MermaidSemanticRole.Node,
                NameIndex: -1,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)]);

        Assert.Equal(
            $"repeat{Environment.NewLine}repeat{Environment.NewLine}outside",
            MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextFollowsDrawCommandOrderWhenSemanticTableOrderDiffers()
    {
        MermaidScene scene = CreateTextScene(
            ["visible title", "node label"],
            [
                CreateTextCommand(0, semanticIndex: 1),
                CreateTextCommand(1, semanticIndex: 0),
            ],
            [
                new MermaidSemanticItem(
                    10,
                    MermaidSemanticRole.Node,
                    NameIndex: 1,
                    -1,
                    -1,
                    -1,
                    MermaidSemanticFlags.None),
                new MermaidSemanticItem(
                    1,
                    MermaidSemanticRole.Diagram,
                    NameIndex: 0,
                    -1,
                    -1,
                    -1,
                    MermaidSemanticFlags.None),
            ]);

        Assert.Equal(
            $"visible title{Environment.NewLine}node label",
            MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextKeepsAccessibleRootNameSeparateFromRenderedContent()
    {
        MermaidScene scene = CreateTextScene(
            ["diagram", "root detail", "decoration", "outside"],
            [
                CreateTextCommand(1, semanticIndex: 0),
                CreateTextCommand(2, semanticIndex: -1, MermaidDrawFlags.Decorative),
                CreateTextCommand(3, semanticIndex: -1),
                CreateTextCommand(3, semanticIndex: -1),
            ],
            [new MermaidSemanticItem(
                1,
                MermaidSemanticRole.Diagram,
                NameIndex: 0,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)]);

        Assert.Equal(
            $"root detail{Environment.NewLine}decoration" +
                $"{Environment.NewLine}outside{Environment.NewLine}outside",
            MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextPreservesRenderedRootTextThatMatchesAccessibleName()
    {
        MermaidScene scene = CreateTextScene(
            ["diagram"],
            [CreateTextCommand(0, semanticIndex: 0)],
            [new MermaidSemanticItem(
                1,
                MermaidSemanticRole.Diagram,
                NameIndex: 0,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)]);

        Assert.Equal("diagram", MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextUsesAccessibleNameWhenSceneHasNoRenderedSemanticContent()
    {
        MermaidScene scene = CreateTextScene(
            ["diagram"],
            [],
            [new MermaidSemanticItem(
                1,
                MermaidSemanticRole.Diagram,
                NameIndex: 0,
                -1,
                -1,
                -1,
                MermaidSemanticFlags.None)]);

        Assert.Equal("diagram", MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    [Fact]
    public void SemanticTextDoesNotCopyNameOnlyShapeMetadataAlongsideRenderedLabels()
    {
        MermaidScene scene = CreateTextScene(
            ["diagram", "shape-only label", "visible title"],
            [CreateTextCommand(2, semanticIndex: 0)],
            [
                new MermaidSemanticItem(
                    1,
                    MermaidSemanticRole.Diagram,
                    NameIndex: 0,
                    -1,
                    -1,
                    -1,
                    MermaidSemanticFlags.None),
                new MermaidSemanticItem(
                    2,
                    MermaidSemanticRole.Node,
                    NameIndex: 1,
                    -1,
                    0,
                    -1,
                    MermaidSemanticFlags.None),
            ]);

        Assert.Equal("visible title", MermaidVectorSceneAdapter.GetSemanticText(scene));
    }

    private static MermaidScene CreateTextScene(
        string[] strings,
        MermaidDrawCommand[] commands,
        MermaidSemanticItem[] semantics)
        => new(
            MermaidSceneVersion.Current,
            new MermaidViewport(0, 0, 100, 100),
            strings,
            [],
            [],
            commands,
            semantics,
            [],
            [],
            []);

    private static MermaidDrawCommand CreateTextCommand(
        int textIndex,
        int semanticIndex,
        MermaidDrawFlags flags = MermaidDrawFlags.None)
        => new(
            MermaidDrawOpcode.Text,
            flags,
            -1,
            semanticIndex,
            textIndex,
            0,
            4,
            -1,
            MermaidTextFlags.None);

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
