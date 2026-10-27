using System.Numerics;
using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Mermaid;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MermaidTypographyRegressionTests
{
    private const string FlowchartSource = """
        flowchart LR
          A(["Invokable Mermaid node"]) --> B[/"Markdown source"/]
        """;

    [Theory]
    [InlineData("Segoe UI", "Consolas")]
    [InlineData("Consolas", "Segoe UI")]
    public async Task MermaidCatalogFontControlsMeasurementAndPaintAndLabelFitsNode(
        string catalogPrimaryFamily,
        string hostThemeFontFamily)
    {
        var catalog = new MermaidFontCatalog([catalogPrimaryFamily, "Segoe UI"]);
        MarkdownVectorScene scene = await ParseSceneAsync(catalog);
        int labelIndex = Array.FindIndex(scene.Commands.ToArray(), static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text!.Contains("Invokable", StringComparison.Ordinal));
        Assert.True(labelIndex >= 0,
            $"The native flowchart did not emit the expected node label. Labels: {string.Join(" | ", scene.Commands.Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText).Select(static command => command.Text))}");

        MarkdownVectorCommand label = scene.Commands[labelIndex];
        int[] labelIndices = scene.Commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Kind == MarkdownVectorCommandKind.DrawText &&
                item.command.SemanticIndex == label.SemanticIndex)
            .Select(static item => item.index)
            .ToArray();
        Assert.NotEmpty(labelIndices);
        foreach (int index in labelIndices)
        {
            MarkdownVectorTextStyle textStyle = scene.Commands[index].TextStyle!;
            Assert.Equal(MarkdownVectorFontRole.Authored, textStyle.FontRole);
            Assert.Equal(catalogPrimaryFamily, textStyle.FontFamily);
            Assert.Equal(catalogPrimaryFamily, VectorSceneDrawing.ResolveFontFamily(textStyle, hostThemeFontFamily));
        }

        int shapeIndex = Array.FindIndex(scene.Commands.ToArray(), command =>
            command.SemanticIndex == label.SemanticIndex &&
            command.Kind is MarkdownVectorCommandKind.DrawPath or MarkdownVectorCommandKind.DrawRectangle);
        Assert.True(shapeIndex >= 0, "The native flowchart did not emit the node's shape.");
        MarkdownVectorCommand shape = scene.Commands[shapeIndex];

        CanvasDevice device = CanvasDevice.GetSharedDevice();
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            hostThemeFontFamily);
        Rect shapeBounds = shape.Kind switch
        {
            MarkdownVectorCommandKind.DrawPath => ComputePathBounds(device, shape),
            MarkdownVectorCommandKind.DrawRectangle => ToRect(shape.Rectangle),
            _ => throw new InvalidOperationException("The selected command is not a node shape."),
        };
        Rect inkBounds = Union(labelIndices.Select(index => GetNativeTextInkBounds(drawing, scene, index)));

        Assert.True(inkBounds.Width > 0 && inkBounds.Height > 0, "The label has no measured ink bounds.");
        Assert.True(inkBounds.Left >= shapeBounds.Left,
            $"Label left edge {inkBounds.Left} escaped node left edge {shapeBounds.Left}.");
        Assert.True(inkBounds.Right <= shapeBounds.Right,
            $"Label right edge {inkBounds.Right} escaped node right edge {shapeBounds.Right}.");
        Assert.True(inkBounds.Top >= shapeBounds.Top,
            $"Label top edge {inkBounds.Top} escaped node top edge {shapeBounds.Top}.");
        Assert.True(inkBounds.Bottom <= shapeBounds.Bottom,
            $"Label bottom edge {inkBounds.Bottom} escaped node bottom edge {shapeBounds.Bottom}.");
    }

    private static async Task<MarkdownVectorScene> ParseSceneAsync(MermaidFontCatalog fontCatalog)
    {
        using var renderer = new MermaidRenderer(fontCatalog: fontCatalog);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{FlowchartSource}\n```");
        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            codeBlock.SourceSpan,
            out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        return Assert.IsType<MarkdownVectorScene>(content.VectorScene);
    }

    private static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static Rect ComputePathBounds(CanvasDevice device, MarkdownVectorCommand command)
    {
        using var builder = new CanvasPathBuilder(device);
        builder.SetFilledRegionDetermination(command.Style?.FillRule == MarkdownVectorFillRule.EvenOdd
            ? CanvasFilledRegionDetermination.Alternate
            : CanvasFilledRegionDetermination.Winding);
        bool figureOpen = false;
        foreach (MarkdownVectorPathOperation operation in command.Path)
        {
            switch (operation.Kind)
            {
                case MarkdownVectorPathOperationKind.Move:
                    if (figureOpen)
                        builder.EndFigure(CanvasFigureLoop.Open);
                    builder.BeginFigure(ToVector(operation.Point1));
                    figureOpen = true;
                    break;
                case MarkdownVectorPathOperationKind.Line when figureOpen:
                    builder.AddLine(ToVector(operation.Point1));
                    break;
                case MarkdownVectorPathOperationKind.QuadraticBezier when figureOpen:
                    builder.AddQuadraticBezier(
                        ToVector(operation.Point1),
                        ToVector(operation.Point2));
                    break;
                case MarkdownVectorPathOperationKind.CubicBezier when figureOpen:
                    builder.AddCubicBezier(
                        ToVector(operation.Point1),
                        ToVector(operation.Point2),
                        ToVector(operation.Point3));
                    break;
                case MarkdownVectorPathOperationKind.Close when figureOpen:
                    builder.EndFigure(CanvasFigureLoop.Closed);
                    figureOpen = false;
                    break;
            }
        }

        if (figureOpen)
            builder.EndFigure(CanvasFigureLoop.Open);
        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        return geometry.ComputeBounds();
    }

    private static Vector2 ToVector(MarkdownVectorPoint point) => new(point.X, point.Y);

    private static Rect GetNativeTextInkBounds(
        VectorSceneDrawing drawing,
        MarkdownVectorScene scene,
        int commandIndex)
    {
        FieldInfo layoutsField = typeof(VectorSceneDrawing).GetField(
            "_textLayouts",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var layouts = (CanvasTextLayout?[])layoutsField.GetValue(drawing)!;
        MarkdownVectorCommand command = scene.Commands[commandIndex];
        Assert.True(HasNativeTextLayout(command),
            $"Command {commandIndex} is not a complete DrawText command.");

        // Native resources are densely packed by type; source-command
        // ordinals remain semantic identities, not native-array indices.
        int textLayoutSlot = 0;
        for (int index = 0; index < commandIndex; index++)
        {
            if (HasNativeTextLayout(scene.Commands[index]))
                textLayoutSlot++;
        }

        Assert.InRange(textLayoutSlot, 0, layouts.Length - 1);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(layouts[textLayoutSlot]);
        MethodInfo resolveOrigin = typeof(VectorSceneDrawing).GetMethod(
            "ResolveTextOrigin",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Vector2 origin = Assert.IsType<Vector2>(resolveOrigin.Invoke(
            null,
            [command, layout]));
        Rect ink = layout.DrawBounds;
        return new Rect(origin.X + ink.X, origin.Y + ink.Y, ink.Width, ink.Height);
    }

    private static bool HasNativeTextLayout(MarkdownVectorCommand command) =>
        command.Kind == MarkdownVectorCommandKind.DrawText &&
        command.Text is not null &&
        command.TextStyle is not null;

    private static Rect Union(IEnumerable<Rect> bounds)
    {
        Rect[] rectangles = bounds.ToArray();
        Assert.NotEmpty(rectangles);
        double left = rectangles.Min(static rectangle => rectangle.Left);
        double top = rectangles.Min(static rectangle => rectangle.Top);
        double right = rectangles.Max(static rectangle => rectangle.Right);
        double bottom = rectangles.Max(static rectangle => rectangle.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }
}
