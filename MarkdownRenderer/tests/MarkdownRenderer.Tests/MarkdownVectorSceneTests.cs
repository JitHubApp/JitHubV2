using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownVectorSceneTests
{
    [Fact]
    public void RichScenePreservesResolvedTextSemanticsAndLinkActionsImmutably()
    {
        var semantics = new List<MarkdownVectorSemanticItem>
        {
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Release flow",
                "A native diagram",
                -1,
                new SourceSpan(10, 20),
                new MarkdownVectorRectangle(0, 0, 320, 120)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Open issue",
                "Invokes the host-approved issue action",
                0,
                new SourceSpan(15, 10),
                new MarkdownVectorRectangle(20, 24, 96, 40),
                MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked),
        };
        var commands = new List<MarkdownVectorCommand>
        {
            MarkdownVectorCommand.BeginGroup(new MarkdownVectorTransform(1, 0, 0, 1, 4, 8), 1),
            MarkdownVectorCommand.DrawText(
                "Open issue",
                new MarkdownVectorPoint(28, 52),
                new MarkdownVectorTextStyle("Segoe UI", 15, 600),
                new MarkdownVectorPaintStyle(fillArgb: 0xff00_0000),
                1),
            MarkdownVectorCommand.EndGroup(),
        };
        var links = new List<MarkdownVectorLinkAction>
        {
            new(1, "https://example.invalid/issue", "open-issue", external: true),
        };

        var scene = new MarkdownVectorScene(
            320,
            120,
            120,
            commands,
            0,
            new MarkdownVectorRectangle(0, 0, 320, 120),
            semantics,
            links);

        commands.Clear();
        semantics.Clear();
        links.Clear();

        Assert.Equal(3, scene.Commands.Count);
        MarkdownVectorCommand text = scene.Commands[1];
        Assert.Equal("Open issue", text.Text);
        Assert.Equal("Segoe UI", text.TextStyle!.FontFamily);
        Assert.Equal(MarkdownVectorFontRole.Authored, text.TextStyle.FontRole);
        Assert.Equal((ushort)600, text.TextStyle.FontWeight);
        Assert.Equal(new SourceSpan(15, 10), scene.Semantics[1].SourceSpan);
        Assert.Equal(MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked,
            scene.Semantics[1].Flags);
        Assert.Equal("https://example.invalid/issue", scene.Links[0].Target);
        Assert.Equal("open-issue", scene.Links[0].Action);
        Assert.True(scene.Links[0].External);
    }

    [Fact]
    public void TextStylePreservesLegacyAuthoredFontsAndSupportsHostFonts()
    {
        var authored = new MarkdownVectorTextStyle("Custom Diagram Face", 16);
        var host = new MarkdownVectorTextStyle(
            MarkdownVectorFontRole.Host,
            "Scene Fallback",
            16);

        Assert.Equal(MarkdownVectorFontRole.Authored, authored.FontRole);
        Assert.Equal(MarkdownVectorFontRole.Host, host.FontRole);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownVectorTextStyle(
            (MarkdownVectorFontRole)99,
            "Scene Fallback",
            16));
    }

    [Fact]
    public void SceneRejectsCrossedOrUnclosedCommandScopes()
    {
        MarkdownVectorPathOperation[] clip =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(10, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(10, 10)),
            MarkdownVectorPathOperation.Close(),
        ];

        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [MarkdownVectorCommand.BeginGroup(MarkdownVectorTransform.Identity)]));
        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [
                MarkdownVectorCommand.BeginGroup(MarkdownVectorTransform.Identity),
                MarkdownVectorCommand.BeginClip(clip),
                MarkdownVectorCommand.EndGroup(),
                MarkdownVectorCommand.EndClip(),
            ]));
    }

    [Fact]
    public void SceneRejectsSemanticCyclesAndDanglingReferences()
    {
        MarkdownVectorSemanticItem[] cycle =
        [
            Semantic(1, parentIndex: 1),
            Semantic(2, parentIndex: 0),
        ];
        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            cycle,
            []));

        MarkdownVectorSemanticItem[] valid = [Semantic(1, parentIndex: -1)];
        var style = new MarkdownVectorPaintStyle(fillArgb: 0xff00_0000);
        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [MarkdownVectorCommand.DrawRectangle(new MarkdownVectorRectangle(0, 0, 10, 10), style, 1)],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            valid,
            []));
        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            valid,
            [new MarkdownVectorLinkAction(1, "https://example.invalid", null)]));
    }

    [Fact]
    public void SceneRejectsLinkedSemanticWithoutAction()
    {
        MarkdownVectorSemanticItem[] semantics =
        [
            Semantic(1, parentIndex: -1, flags: MarkdownVectorSemanticFlags.Linked),
        ];

        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            semantics,
            []));
    }

    [Fact]
    public void SceneRejectsActionForSemanticWithoutLinkedFlag()
    {
        MarkdownVectorSemanticItem[] semantics = [Semantic(1, parentIndex: -1)];

        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            semantics,
            [new MarkdownVectorLinkAction(0, "https://example.invalid", null)]));
    }

    [Fact]
    public void SceneRejectsDuplicateActionsForOneLinkedSemantic()
    {
        MarkdownVectorSemanticItem[] semantics =
        [
            Semantic(1, parentIndex: -1, flags: MarkdownVectorSemanticFlags.Linked),
        ];

        Assert.Throws<ArgumentException>(() => new MarkdownVectorScene(
            10,
            10,
            10,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 10, 10),
            semantics,
            [
                new MarkdownVectorLinkAction(0, "https://example.invalid/one", null),
                new MarkdownVectorLinkAction(0, "https://example.invalid/two", null),
            ]));
    }

    private static MarkdownVectorSemanticItem Semantic(
        uint sourceId,
        int parentIndex,
        MarkdownVectorSemanticFlags flags = MarkdownVectorSemanticFlags.None) => new(
        sourceId,
        MarkdownVectorSemanticRole.Node,
        $"Node {sourceId}",
        null,
        parentIndex,
        new SourceSpan(0, 1),
        new MarkdownVectorRectangle(0, 0, 1, 1),
        flags);
}
