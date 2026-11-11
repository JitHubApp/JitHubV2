using MarkdownRenderer.Mermaid.Internal.Rendering;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Tests.NativeRuntime;

/// <summary>
/// Paint that Mermaid authors through SVG indirection (paint servers, markers)
/// must survive the native compiler as visible MMIR paint. Sources are the
/// Sample app's Mermaid page diagrams.
/// </summary>
public sealed class MermaidNativePaintFidelityTests
{
    private const string SankeySource =
        "sankey-beta\n\nMarkdown,Parser,10\nParser,Layout,8\nParser,Fallback,2\nLayout,Win2D,8\n";

    private const string AutonumberSequenceSource =
        "sequenceDiagram\n" +
        "  autonumber\n" +
        "  actor User\n" +
        "  participant App as JitHub\n" +
        "  participant Renderer\n" +
        "  User->>App: Open README\n" +
        "  App->>+Renderer: Parse Markdown\n" +
        "  alt Supported syntax\n" +
        "    Renderer-->>App: Native semantic scene\n" +
        "  else Invalid syntax\n" +
        "    Renderer-->>-App: Exact fallback and diagnostic\n" +
        "  end\n" +
        "  Note over App,Renderer: No browser or JavaScript runtime\n";

    [Fact]
    public async Task SankeyGradientLinksArePaintedBetweenTheirNodes()
    {
        MermaidScene scene = await RenderAsync(SankeySource);

        // Mermaid strokes each flow band with a source-to-target gradient at
        // stroke-opacity 0.5; its width is the flow value scaled to the layout.
        (MermaidDrawCommand Command, MermaidSceneStyle Style)[] links = scene.Commands
            .Where(static command => command.Opcode == MermaidDrawOpcode.StrokePath && command.StyleIndex >= 0)
            .Select(command => (command, scene.Styles[command.StyleIndex]))
            .Where(static link => link.Item2.StrokeWidth > 10)
            .OrderByDescending(static link => link.Item2.StrokeWidth)
            .ToArray();

        Assert.Equal(4, links.Length);
        float widest = links[0].Style.StrokeWidth;
        Assert.Equal(
            [1f, 0.8f, 0.8f, 0.2f],
            links.Select(link => MathF.Round(link.Style.StrokeWidth / widest, 3)));
        Assert.All(links, static link =>
        {
            Assert.Equal((byte)128, link.Style.Stroke.Alpha);
            Assert.Equal((byte)0, link.Style.Fill.Alpha);
        });

        // Markdown -> Parser: the mean of Tableau #4e79a7 and #f28e2c.
        MermaidRgba32 markdownToParser = links[0].Style.Stroke;
        Assert.InRange(markdownToParser.Red, (byte)0x9f, (byte)0xa1);
        Assert.InRange(markdownToParser.Green, (byte)0x83, (byte)0x84);
        Assert.InRange(markdownToParser.Blue, (byte)0x69, (byte)0x6a);
    }

    [Fact]
    public async Task AutonumberedMessagesPaintEachStepNumberOnItsBadge()
    {
        MermaidScene scene = await RenderAsync(AutonumberSequenceSource);

        for (int step = 1; step <= 4; step++)
        {
            int textCommand = IndexOfText(scene, step.ToString(System.Globalization.CultureInfo.InvariantCulture));
            MermaidDrawCommand text = scene.Commands[textCommand];
            float x = scene.Geometry[text.GeometryStart];
            float baseline = scene.Geometry[text.GeometryStart + 1];
            MermaidRgba32 ink = scene.Styles[text.StyleIndex].Fill;

            // Mermaid draws the badge as a marker-start circle on a
            // zero-length line, before the number, in a contrasting fill.
            bool hasBadge = scene.Commands
                .Take(textCommand)
                .Where(static command => command.Opcode == MermaidDrawOpcode.Ellipse && command.StyleIndex >= 0)
                .Any(command =>
                {
                    float left = scene.Geometry[command.GeometryStart];
                    float top = scene.Geometry[command.GeometryStart + 1];
                    float width = scene.Geometry[command.GeometryStart + 2];
                    float height = scene.Geometry[command.GeometryStart + 3];
                    MermaidRgba32 fill = scene.Styles[command.StyleIndex].Fill;
                    return x > left && x < left + width &&
                        baseline > top && baseline < top + height &&
                        fill.Alpha == 255 && fill != ink;
                });
            Assert.True(hasBadge, $"Step {step} has no contrasting badge under its number.");
        }
    }

    private static async Task<MermaidScene> RenderAsync(string source)
    {
        using var renderer = new MermaidRenderer();
        MermaidRenderResult result = await renderer.RenderAsync(source);
        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        return Assert.IsType<MermaidScene>(result.Scene);
    }

    private static int IndexOfText(MermaidScene scene, string value)
    {
        for (int index = 0; index < scene.Commands.Count; index++)
        {
            MermaidDrawCommand command = scene.Commands[index];
            if (command.Opcode == MermaidDrawOpcode.Text && scene.Strings[command.TextIndex] == value)
            {
                return index;
            }
        }

        Assert.Fail($"No text command paints '{value}'.");
        return -1;
    }
}
