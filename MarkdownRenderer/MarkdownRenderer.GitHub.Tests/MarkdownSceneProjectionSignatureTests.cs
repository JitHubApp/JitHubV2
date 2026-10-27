using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSceneProjectionSignatureTests
{
    [Fact]
    public void RecreatedEquivalentLeavesHaveEqualSignaturesRegardlessOfAttributeOrder()
    {
        MarkdownContent first = CreateContent(
            scene: CreateScene(),
            attributes: CreateAttributes(reverse: false));
        MarkdownContent second = CreateContent(
            scene: CreateScene(),
            attributes: CreateAttributes(reverse: true));

        MarkdownSceneProjectionSignature firstSignature = Sign(first);
        MarkdownSceneProjectionSignature secondSignature = Sign(second);

        Assert.Equal(firstSignature, secondSignature);
        Assert.Equal(firstSignature.GetHashCode(), secondSignature.GetHashCode());
    }

    [Fact]
    public void ContentProjectionMetadataChangesTheSignature()
    {
        MarkdownSceneProjectionSignature baseline = Sign(CreateContent());
        MarkdownContent[] changedContent =
        [
            CreateContent(sourceSpan: new SourceSpan(5, 9)),
            CreateContent(styleRole: MarkdownStyleRole.Diagram),
            CreateContent(accessibilityRole: MarkdownAccessibilityRole.Diagram),
            CreateContent(text: null),
            CreateContent(text: string.Empty),
            CreateContent(destination: null),
            CreateContent(language: "fr"),
            CreateContent(factoryKey: "other-factory"),
            CreateContent(customKind: "other-custom"),
            CreateContent(accessibilityName: "other name"),
            CreateContent(accessibilityDescription: "other description"),
            CreateContent(semanticText: "other semantic text"),
            CreateContent(attributes: CreateAttributes(value: "changed")),
        ];

        foreach (MarkdownContent content in changedContent)
            Assert.NotEqual(baseline, Sign(content));
    }

    [Fact]
    public void SceneProjectionChangesAcrossGeometrySemanticsStylesLinksAndThemeRoles()
    {
        MarkdownSceneProjectionSignature baseline = Sign(CreateContent());
        MarkdownContent[] changedScenes =
        [
            CreateContent(scene: CreateScene(width: 41)),
            CreateContent(scene: CreateScene(height: 25)),
            CreateContent(scene: CreateScene(baseline: 15)),
            CreateContent(scene: CreateScene(referenceFontSize: 19)),
            CreateContent(scene: CreateScene(viewport: new MarkdownVectorRectangle(-3, -1, 44, 26))),
            CreateContent(scene: CreateScene(pathEndX: 15)),
            CreateContent(scene: CreateScene(commandText: "different text")),
            CreateContent(scene: CreateScene(commandSemanticIndex: 0)),
            CreateContent(scene: CreateScene(isClosed: false)),
            CreateContent(scene: CreateScene(transform: new MarkdownVectorTransform(1, 0, 0, 1, 9, 6))),
            CreateContent(scene: CreateScene(strokeWidth: 3)),
            CreateContent(scene: CreateScene(fillRole: MarkdownVectorPaintRole.Surface)),
            CreateContent(scene: CreateScene(highContrastFillRole: MarkdownVectorPaintRole.Foreground)),
            CreateContent(scene: CreateScene(fontRole: MarkdownVectorFontRole.Authored)),
            CreateContent(scene: CreateScene(textFontFamily: "Consolas")),
            CreateContent(scene: CreateScene(textFontSize: 13)),
            CreateContent(scene: CreateScene(textFontWeight: 600)),
            CreateContent(scene: CreateScene(textItalic: false)),
            CreateContent(scene: CreateScene(textRightToLeft: false)),
            CreateContent(scene: CreateScene(textAlignment: MarkdownVectorTextAlignment.Center)),
            CreateContent(scene: CreateScene(semanticName: "renamed node")),
            CreateContent(scene: CreateScene(semanticDescription: "changed node description")),
            CreateContent(scene: CreateScene(semanticBounds: new MarkdownVectorRectangle(2, 3, 8, 5))),
            CreateContent(scene: CreateScene(semanticFlags: MarkdownVectorSemanticFlags.Linked)),
            CreateContent(scene: CreateScene(linkTarget: "/other-node")),
            CreateContent(scene: CreateScene(linkAction: "select")),
            CreateContent(scene: CreateScene(linkExternal: false)),
        ];

        foreach (MarkdownContent content in changedScenes)
            Assert.NotEqual(baseline, Sign(content));
    }

    [Fact]
    public void FloatEncodingPreservesNegativeZero()
    {
        MarkdownSceneProjectionSignature positiveZero = Sign(CreateContent(scene: CreateScene(width: 0f)));
        MarkdownSceneProjectionSignature negativeZero = Sign(CreateContent(scene: CreateScene(width: -0f)));

        Assert.NotEqual(positiveZero, negativeZero);
    }

    [Fact]
    public void LongStringsAreStreamedAndRemainOrdinal()
    {
        string longText = new string('a', 16_385) + "\uD83D\uDE80";
        MarkdownSceneProjectionSignature first = Sign(CreateContent(semanticText: longText));
        MarkdownSceneProjectionSignature recreated = Sign(CreateContent(semanticText: new string('a', 16_385) + "\uD83D\uDE80"));
        MarkdownSceneProjectionSignature changed = Sign(CreateContent(semanticText: longText + "!"));

        Assert.Equal(first, recreated);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void CancellationIsObservedAtEntryForEmptyScenesAndLongStrings()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MarkdownContent emptyScene = CreateContent(scene: CreateScene(emptyCommands: true));
        MarkdownContent longString = CreateContent(semanticText: new string('x', 500_000));

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownSceneProjectionSignature.Create(emptyScene, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            MarkdownSceneProjectionSignature.Create(longString, cancellation.Token));
    }

    [Fact]
    public void RejectsNonSceneContentAndNonLeafContent()
    {
        MarkdownContent valid = CreateContent();
        var nonScene = new MarkdownContent(
            MarkdownContentKind.Text,
            valid.StyleRole,
            valid.SourceSpan,
            valid.AccessibilityRole,
            valid.Text,
            valid.Destination,
            valid.Language,
            valid.FactoryKey,
            valid.CustomKind,
            valid.VectorScene,
            valid.Attributes,
            Array.Empty<MarkdownContent>(),
            valid.AccessibilityName,
            valid.AccessibilityDescription,
            valid.SemanticText);
        MarkdownContent nonLeaf = CreateContent(children: [valid]);

        Assert.Throws<ArgumentException>(() => Sign(nonScene));
        Assert.Throws<ArgumentException>(() => Sign(nonLeaf));
    }

    private static MarkdownSceneProjectionSignature Sign(MarkdownContent content) =>
        MarkdownSceneProjectionSignature.Create(content, CancellationToken.None);

    private static MarkdownContent CreateContent(
        MarkdownVectorScene? scene = null,
        SourceSpan? sourceSpan = null,
        MarkdownStyleRole? styleRole = null,
        MarkdownAccessibilityRole? accessibilityRole = null,
        string? text = "text payload",
        string? destination = "https://example.test/target",
        string? language = "en-US",
        string? factoryKey = "factory-key",
        string? customKind = "custom-kind",
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyList<MarkdownContent>? children = null,
        string? accessibilityName = "scene name",
        string? accessibilityDescription = "scene description",
        string? semanticText = "scene semantic text") =>
        new(
            MarkdownContentKind.VectorScene,
            styleRole ?? MarkdownStyleRole.Math,
            sourceSpan ?? new SourceSpan(4, 9),
            accessibilityRole ?? MarkdownAccessibilityRole.Math,
            text,
            destination,
            language,
            factoryKey,
            customKind,
            scene ?? CreateScene(),
            attributes ?? CreateAttributes(),
            children ?? Array.Empty<MarkdownContent>(),
            accessibilityName,
            accessibilityDescription,
            semanticText);

    private static IReadOnlyDictionary<string, string> CreateAttributes(
        bool reverse = false,
        string value = "attribute-value")
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reverse)
        {
            attributes.Add("zeta", "tail");
            attributes.Add("alpha", value);
        }
        else
        {
            attributes.Add("alpha", value);
            attributes.Add("zeta", "tail");
        }
        return new ReadOnlyDictionary<string, string>(attributes);
    }

    private static MarkdownVectorScene CreateScene(
        float width = 40,
        float height = 24,
        float baseline = 16,
        float referenceFontSize = 18,
        MarkdownVectorRectangle? viewport = null,
        float pathEndX = 14,
        float strokeWidth = 2,
        MarkdownVectorPaintRole fillRole = MarkdownVectorPaintRole.Foreground,
        MarkdownVectorPaintRole highContrastFillRole = MarkdownVectorPaintRole.Link,
        MarkdownVectorFontRole fontRole = MarkdownVectorFontRole.Host,
        MarkdownVectorTransform? transform = null,
        string commandText = "node label",
        int commandSemanticIndex = 1,
        bool isClosed = true,
        string textFontFamily = "Segoe UI",
        float textFontSize = 12.5f,
        ushort textFontWeight = 700,
        bool textItalic = true,
        bool textRightToLeft = true,
        MarkdownVectorTextAlignment textAlignment = MarkdownVectorTextAlignment.End,
        string semanticName = "node",
        string semanticDescription = "node description",
        MarkdownVectorRectangle? semanticBounds = null,
        MarkdownVectorSemanticFlags semanticFlags =
            MarkdownVectorSemanticFlags.Focusable |
            MarkdownVectorSemanticFlags.Selectable |
            MarkdownVectorSemanticFlags.Linked,
        string linkTarget = "https://example.test/node",
        string linkAction = "open",
        bool linkExternal = true,
        bool emptyCommands = false)
    {
        var paint = MarkdownVectorPaintStyle.CreateSemantic(
                fillRole,
                MarkdownVectorPaintRole.Link,
                fillArgb: 0xFF2468AC,
                strokeArgb: 0xFF13579B,
                strokeWidth: strokeWidth,
                opacity: 0.75f,
                fillRule: MarkdownVectorFillRule.EvenOdd,
                lineCap: MarkdownVectorLineCap.Round,
                lineJoin: MarkdownVectorLineJoin.Bevel,
                nonScalingStroke: true,
                dashArray: [1f, 2f, 3f],
                dashOffset: 0.5f)
            .WithHighContrastRoles(highContrastFillRole, MarkdownVectorPaintRole.Foreground);
        var textStyle = new MarkdownVectorTextStyle(
            fontRole,
            textFontFamily,
            textFontSize,
            textFontWeight,
            textItalic,
            textRightToLeft,
            textAlignment);
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 2)),
            MarkdownVectorPathOperation.QuadraticBezierTo(
                new MarkdownVectorPoint(4, 5),
                new MarkdownVectorPoint(7, 6)),
            MarkdownVectorPathOperation.CubicBezierTo(
                new MarkdownVectorPoint(8, 7),
                new MarkdownVectorPoint(10, 8),
                new MarkdownVectorPoint(pathEndX, 9)),
            MarkdownVectorPathOperation.Close(),
        ];
        var rectangle = new MarkdownVectorRectangle(2, 3, 10, 6);
        IReadOnlyList<MarkdownVectorCommand> commands = emptyCommands
            ? Array.Empty<MarkdownVectorCommand>()
            : new MarkdownVectorCommand[]
            {
                MarkdownVectorCommand.BeginGroup(
                    transform ?? new MarkdownVectorTransform(1.1f, 0.2f, -0.3f, 0.9f, 5, 6),
                    semanticIndex: 0),
                MarkdownVectorCommand.FillPath(path, 0xFF112233),
                MarkdownVectorCommand.StrokePath(path, 1.25f, 0xFF334455),
                MarkdownVectorCommand.FillRectangle(rectangle, 0xFF556677),
                MarkdownVectorCommand.StrokeRectangle(rectangle, 0.75f, 0xFF778899),
                MarkdownVectorCommand.StrokeLine(
                    new MarkdownVectorPoint(3, 4),
                    new MarkdownVectorPoint(8, 9),
                    1.5f,
                    0xFF99AABB),
                MarkdownVectorCommand.DrawRectangle(rectangle, paint, semanticIndex: 1),
                MarkdownVectorCommand.DrawPath(path, paint, isClosed, commandSemanticIndex),
                MarkdownVectorCommand.DrawLine(
                    new MarkdownVectorPoint(4, 5),
                    new MarkdownVectorPoint(9, 10),
                    paint,
                    semanticIndex: 1),
                MarkdownVectorCommand.DrawEllipse(rectangle, paint, semanticIndex: 1),
                MarkdownVectorCommand.DrawText(
                    commandText,
                    new MarkdownVectorPoint(5, 11),
                    textStyle,
                    paint,
                    semanticIndex: 1),
                MarkdownVectorCommand.EndGroup(),
                MarkdownVectorCommand.BeginClip(path),
                MarkdownVectorCommand.EndClip(),
            };
        MarkdownVectorSemanticFlags rootFlags =
            MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Selectable;
        MarkdownVectorSemanticItem[] semantics =
        [
            new MarkdownVectorSemanticItem(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "root",
                "root description",
                -1,
                new SourceSpan(0, 12),
                new MarkdownVectorRectangle(0, 0, width, height),
                rootFlags),
            new MarkdownVectorSemanticItem(
                2,
                MarkdownVectorSemanticRole.Node,
                semanticName,
                semanticDescription,
                0,
                new SourceSpan(2, 5),
                semanticBounds ?? rectangle,
                semanticFlags),
        ];
        MarkdownVectorLinkAction[] links =
        [
            new MarkdownVectorLinkAction(1, linkTarget, linkAction, linkExternal),
        ];

        return new MarkdownVectorScene(
            width,
            height,
            baseline,
            commands,
            referenceFontSize,
            viewport ?? new MarkdownVectorRectangle(-2, -1, width + 4, height + 2),
            semantics,
            links);
    }
}
