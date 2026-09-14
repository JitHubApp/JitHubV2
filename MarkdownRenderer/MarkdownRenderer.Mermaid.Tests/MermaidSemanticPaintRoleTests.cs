using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidSemanticPaintRoleTests
{
    private const string Source = "flowchart LR\n  A[Start] --> B[Done]";

    [Theory]
    [InlineData(MermaidThemeVariant.Default)]
    [InlineData(MermaidThemeVariant.Light)]
    [InlineData(MermaidThemeVariant.Dark)]
    [InlineData(MermaidThemeVariant.HighContrast)]
    [InlineData(MermaidThemeVariant.Forest)]
    [InlineData(MermaidThemeVariant.Neutral)]
    public async Task ThemesRetainTheirAuthoredPalette(MermaidThemeVariant theme)
    {
        MarkdownVectorScene scene = await ParseSceneAsync(theme);

        Assert.All(
            scene.Commands.Where(static command => command.Style is not null),
            static command =>
            {
                Assert.Equal(MarkdownVectorPaintRole.Authored, command.Style!.FillRole);
                Assert.Equal(MarkdownVectorPaintRole.Authored, command.Style.StrokeRole);
                Assert.NotNull(command.Style.HighContrastFillRole);
                Assert.NotNull(command.Style.HighContrastStrokeRole);
            });
    }

    [Fact]
    public async Task DefaultThemeRetainsPinnedOfficialFlowchartColors()
    {
        MarkdownVectorScene scene = await ParseSceneAsync(MermaidThemeVariant.Default);

        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            command.Style?.FillArgb == 0xFFFFFFFFu);
        MarkdownVectorCommand[] nodes = scene.Commands.Where(command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Node)).ToArray();
        Assert.True(nodes.Any(static command =>
                command.Style?.FillArgb == 0xFFECECFFu &&
                command.Style.StrokeArgb == 0xFF9370DBu),
            "Expected the pinned #ECECFF/#9370DB node palette; got " +
            string.Join(", ", nodes.Select(static command =>
                $"{command.Style?.FillArgb:X8}/{command.Style?.StrokeArgb:X8}")));
        Assert.Contains(scene.Commands, command =>
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Edge) &&
            command.Style?.StrokeArgb == 0xFF333333u);
        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Style?.FillArgb == 0xFF333333u);
    }

    [Fact]
    public async Task DarkThemeRetainsPinnedOfficialFlowchartColors()
    {
        MarkdownVectorScene scene = await ParseSceneAsync(MermaidThemeVariant.Dark);

        Assert.True(scene.Commands.Any(static command =>
                command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
                command.Style?.FillArgb == 0xFF333333u),
            "Expected the pinned #333 canvas; rectangle colors were " +
            string.Join(", ", scene.Commands
                .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawRectangle)
                .Select(static command => $"{command.Style?.FillArgb:X8}/{command.Style?.StrokeArgb:X8}")));
        Assert.Contains(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Node) &&
            command.Style?.FillArgb == 0xFF1F2020u &&
            command.Style.StrokeArgb == 0xFFCCCCCCu);
        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Style?.FillArgb == 0xFFCCCCCCu);
        Assert.Contains(scene.Commands, command =>
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Edge) &&
            command.Style?.StrokeArgb == 0xFFD3D3D3u);
        Assert.Contains(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Edge) &&
            command.Style?.FillArgb == 0xFFD3D3D3u);
    }

    [Fact]
    public async Task DefaultSequenceRetainsPinnedOfficialNoteColors()
    {
        MarkdownVectorScene scene = await ParseSceneAsync(
            MermaidThemeVariant.Default,
            "sequenceDiagram\n  participant A as Alice\n  participant B as Bob\n  Note over A,B: Accessible note\n  A->>B: Hello");

        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            command.Style?.FillArgb == 0xFFFFF5ADu &&
            command.Style.StrokeArgb == 0xFFAAAA33u);
        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Style?.FillArgb == 0xFF000000u);
    }

    [Fact]
    public async Task DefaultPieRetainsDistinctOfficialCategoryColors()
    {
        MarkdownVectorScene scene = await ParseSceneAsync(
            MermaidThemeVariant.Default,
            "pie title Delivery\n  \"Complete\" : 60\n  \"In progress\" : 30\n  \"Planned\" : 10");

        uint[] categoryFills = scene.Commands
            .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawPath &&
                                     command.Style?.FillArgb is { } color &&
                                     (color >> 24) != 0)
            .Select(static command => command.Style!.FillArgb!.Value)
            .Distinct()
            .ToArray();

        Assert.True(
            categoryFills.Length >= 3,
            $"Expected at least three Mermaid category colors, got {string.Join(", ", categoryFills.Select(static color => $"0x{color:X8}"))}.");
    }

    [Fact]
    public async Task MermaidPublishesExplicitWindowsHighContrastRoles()
    {
        MarkdownVectorScene scene = await ParseSceneAsync(MermaidThemeVariant.Default);

        Assert.Contains(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawRectangle &&
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Node) &&
            command.Style?.HighContrastFillRole == MarkdownVectorPaintRole.Surface &&
            command.Style.HighContrastStrokeRole == MarkdownVectorPaintRole.Foreground);
        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Style?.HighContrastFillRole == MarkdownVectorPaintRole.Foreground);
        Assert.Contains(scene.Commands, command =>
            command.Kind == MarkdownVectorCommandKind.DrawPath &&
            IsSemanticRole(scene, command, MarkdownVectorSemanticRole.Edge) &&
            command.Style?.HighContrastFillRole == MarkdownVectorPaintRole.Foreground);
    }

    [Theory]
    [InlineData(MermaidThemeVariant.Default)]
    [InlineData(MermaidThemeVariant.Light)]
    [InlineData(MermaidThemeVariant.Dark)]
    [InlineData(MermaidThemeVariant.HighContrast)]
    [InlineData(MermaidThemeVariant.Forest)]
    [InlineData(MermaidThemeVariant.Neutral)]
    public async Task MermaidTextUsesTheHostDiagramFontRole(MermaidThemeVariant theme)
    {
        MarkdownVectorScene scene = await ParseSceneAsync(theme);
        MarkdownVectorCommand[] textCommands = scene.Commands
            .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
            .ToArray();

        Assert.NotEmpty(textCommands);
        Assert.All(
            textCommands,
            static command => Assert.Equal(
                MarkdownVectorFontRole.Host,
                command.TextStyle!.FontRole));
    }

    private static bool IsSemanticRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        MarkdownVectorSemanticRole role) =>
        command.SemanticIndex >= 0 &&
        command.SemanticIndex < scene.Semantics.Count &&
        scene.Semantics[command.SemanticIndex].Role == role;

    private static async Task<MarkdownVectorScene> ParseSceneAsync(
        MermaidThemeVariant theme,
        string source = Source)
    {
        var options = MermaidRenderOptions.Default with { Theme = theme };
        using var renderer = new MermaidRenderer(options);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");
        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        return Assert.IsType<MarkdownVectorScene>(Assert.Single(fragment!.Items).VectorScene);
    }
}
