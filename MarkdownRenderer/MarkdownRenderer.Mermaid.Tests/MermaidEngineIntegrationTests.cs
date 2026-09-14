using System.Globalization;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidEngineIntegrationTests
{
    private const string DiagramSource = "flowchart LR\n  A[Start] --> B[Done]";

    [Fact]
    public async Task MermaidFenceEmitsRichReusableVectorScene()
    {
        string markdown = $"```mermaid\n{DiagramSource}\n```";
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, content.Kind);
        Assert.Equal(MarkdownAccessibilityRole.Diagram, content.AccessibilityRole);
        Assert.Equal(MarkdownStyleRole.Diagram, content.StyleRole);
        Assert.Equal(DiagramSource, content.AccessibilityDescription);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        Assert.Contains("Start", semanticText, StringComparison.Ordinal);
        Assert.Contains("Done", semanticText, StringComparison.Ordinal);

        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.True(scene.Width > 0);
        Assert.True(scene.Height > 0);
        Assert.Contains(scene.Commands, static command => command.Kind == MarkdownVectorCommandKind.DrawText);
        Assert.Contains(scene.Commands, static command => command.Kind is MarkdownVectorCommandKind.DrawPath or
            MarkdownVectorCommandKind.DrawRectangle or MarkdownVectorCommandKind.DrawEllipse);
        Assert.Contains(scene.Semantics, static semantic => semantic.Role == MarkdownVectorSemanticRole.Diagram);
        Assert.Contains(scene.Semantics, static semantic => semantic.Role == MarkdownVectorSemanticRole.Node);
        Assert.Equal(scene.Semantics.Count, scene.Semantics.Select(static semantic => semantic.SourceId).Distinct().Count());
        Assert.All(scene.Semantics, semantic =>
        {
            Assert.InRange(semantic.SourceSpan.Start, codeBlock.SourceSpan.Start, codeBlock.SourceSpan.End);
            Assert.InRange(semantic.SourceSpan.End, semantic.SourceSpan.Start, codeBlock.SourceSpan.End);
        });

        MarkdownVectorCommand text = scene.Commands.First(static command => command.Kind == MarkdownVectorCommandKind.DrawText);
        Assert.NotNull(text.TextStyle);
        Assert.False(string.IsNullOrWhiteSpace(text.TextStyle!.FontFamily));
        Assert.InRange(text.TextStyle.FontWeight, (ushort)1, (ushort)999);
    }

    [Fact]
    public async Task MermaidLinkSurvivesTheDeclarativeVectorSceneAdapter()
    {
        const string target = "https://example.invalid/mermaid-node";
        const string source = "flowchart LR\n  A[Invokable Mermaid node] --> B[Native scene]\n  click A \"https://example.invalid/mermaid-node\" \"Open Mermaid node\"";
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(Assert.Single(fragment!.Items).VectorScene);
        MarkdownVectorLinkAction link = Assert.Single(scene.Links);
        Assert.Equal(target, link.Target);
        Assert.Null(link.Action);
        Assert.True(link.External);
        MarkdownVectorSemanticItem semantic = scene.Semantics[link.SemanticIndex];
        Assert.Equal(MarkdownVectorSemanticRole.Node, semantic.Role);
        Assert.Contains("Invokable Mermaid node", semantic.Name, StringComparison.Ordinal);
        Assert.Equal(
            MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked,
            semantic.Flags & (MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked));
        Assert.True(semantic.Bounds.Width > 0);
        Assert.InRange(semantic.SourceSpan.Start, codeBlock.SourceSpan.Start, codeBlock.SourceSpan.End);
        Assert.InRange(semantic.SourceSpan.End, semantic.SourceSpan.Start, codeBlock.SourceSpan.End);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task LinkedSceneRemainsIntactBesideAnElkFallbackInOneDocument(string lineEnding)
    {
        const string markdown = """
            # Native Mermaid

            ```mermaid
            flowchart LR
              A[Invokable Mermaid node] --> B[Native scene]
              click A "https://example.invalid/mermaid-node" "Open Mermaid node"
            ```

            ```mermaid
            ---
            config:
              layout: elk
            ---
            flowchart LR
              C --> D
            ```
            """;
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .AddProfile(MarkdownProfiles.MarkdownExtra)
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown.ReplaceLineEndings(lineEnding));

        MarkdownCodeBlock[] blocks = document.GetCodeBlocks().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.True(document.TryGetBlockExtensionContent(blocks[0].SourceSpan, out MarkdownContentFragment? diagram));
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(Assert.Single(diagram!.Items).VectorScene);
        MarkdownVectorLinkAction link = Assert.Single(scene.Links);
        MarkdownVectorSemanticItem semantic = scene.Semantics[link.SemanticIndex];
        Assert.Equal(MarkdownVectorSemanticRole.Node, semantic.Role);
        Assert.Contains("Invokable Mermaid node", semantic.Name, StringComparison.Ordinal);
        Assert.Equal(
            MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked,
            semantic.Flags & (MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked));
        Assert.True(document.TryGetBlockExtensionContent(blocks[1].SourceSpan, out MarkdownContentFragment? fallback));
        Assert.Equal(MarkdownContentKind.CodeBlock, Assert.Single(fallback!.Items).Kind);
    }

    [Fact]
    public async Task ElkOptionFallsBackToExactOriginalCodeBlock()
    {
        var options = MermaidRenderOptions.Default with { Layout = MermaidLayoutMode.Elk };
        using var renderer = new MermaidRenderer(options);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        string markdown = $"```mermaid\n{DiagramSource}\n```";

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, content.Kind);
        Assert.Equal("mermaid", content.Language);
        Assert.Equal(DiagramSource, content.Text);
        Assert.Contains(document.Diagnostics, static diagnostic => diagnostic.Code == "MMR0002");
    }

    [Fact]
    public async Task EmbeddedElkDirectiveFallsBackBeforeMermanLayout()
    {
        const string source = "---\nconfig:\n  layout: elk\n---\nflowchart LR\nA-->B";
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        string markdown = $"```mermaid\n{source}\n```";

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, content.Kind);
        Assert.Equal(source, content.Text);
        Assert.Contains(document.Diagnostics, static diagnostic => diagnostic.Code == "MMR0002");
    }

    [Fact]
    public async Task NonMermaidFenceRemainsOrdinaryCode()
    {
        const string source = "Console.WriteLine(\"native\");";
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```csharp\n{source}\n```");

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.False(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out _));
        Assert.Equal("csharp", codeBlock.Language);
        Assert.Equal(source, codeBlock.DisplayText);
    }

    [Fact]
    public async Task MermaidAndAnotherFencedCodeExtensionCoexistInRegistrationOrder()
    {
        using var renderer = new MermaidRenderer();
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .UseExtension(new TestFenceExtension())
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync("```hosted\nNative control\n```");

        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.HostedElement, content.Kind);
        Assert.Equal("tests.hosted", content.FactoryKey);
        Assert.Equal("Native control", content.SemanticText);
    }

    [Fact]
    public async Task OwnedMermaidFactoryPreservesItsDeclaredFencedRendererPosition()
    {
        var fallbackOptions = MermaidRenderOptions.Default with { Layout = MermaidLayoutMode.Elk };
        using MarkdownEngine mermaidFirst = new MarkdownEngineBuilder()
            .UseMermaid(fallbackOptions)
            .UseExtension(new TestMermaidFenceExtension())
            .WithParseCacheBudgetBytes(0)
            .Build();
        using MarkdownEngine customFirst = new MarkdownEngineBuilder()
            .UseExtension(new TestMermaidFenceExtension())
            .UseMermaid(fallbackOptions)
            .WithParseCacheBudgetBytes(0)
            .Build();
        string markdown = $"```mermaid\n{DiagramSource}\n```";

        Assert.Equal(
            ["MarkdownRenderer.Mermaid", "tests.custom-mermaid-fence"],
            mermaidFirst.Extensions.ExtensionIds);
        Assert.Equal(
            ["tests.custom-mermaid-fence", "MarkdownRenderer.Mermaid"],
            customFirst.Extensions.ExtensionIds);

        MarkdownDocument mermaidDocument = await mermaidFirst.ParseAsync(markdown);
        MarkdownCodeBlock mermaidBlock = Assert.Single(mermaidDocument.GetCodeBlocks());
        Assert.True(mermaidDocument.TryGetBlockExtensionContent(
            mermaidBlock.SourceSpan,
            out MarkdownContentFragment? mermaidFragment));
        MarkdownContent mermaidContent = Assert.Single(mermaidFragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, mermaidContent.Kind);
        Assert.Equal(DiagramSource, mermaidContent.Text);
        Assert.Contains(mermaidDocument.Diagnostics, static diagnostic => diagnostic.Code == "MMR0002");

        MarkdownDocument customDocument = await customFirst.ParseAsync(markdown);
        MarkdownCodeBlock customBlock = Assert.Single(customDocument.GetCodeBlocks());
        Assert.True(customDocument.TryGetBlockExtensionContent(
            customBlock.SourceSpan,
            out MarkdownContentFragment? customFragment));
        MarkdownContent customContent = Assert.Single(customFragment!.Items);
        Assert.Equal(MarkdownContentKind.Text, customContent.Kind);
        Assert.Equal("custom-mermaid", customContent.Text);
        Assert.DoesNotContain(customDocument.Diagnostics, static diagnostic => diagnostic.Code == "MMR0002");
    }

    [Fact]
    public async Task ExtensionOwnedDiagnosticUsesTheConfiguredStringProvider()
    {
        const string markdown = "```mermaid\nflowchart LR\nA-->B\n```";
        var options = MermaidRenderOptions.Default with
        {
            StringProvider = new RendererUnavailableStringProvider(),
        };
        var renderer = new MermaidRenderer(options);
        renderer.Dispose();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownDiagnostic diagnostic = Assert.Single(
            document.Diagnostics,
            static value => value.Code == "MMR0006");
        Assert.Equal("localized-renderer-unavailable", diagnostic.Message);
        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        Assert.Equal(MarkdownContentKind.CodeBlock, Assert.Single(fragment!.Items).Kind);
    }

    [Fact]
    public async Task CachedDocumentDiagnosticsUseTheRendererConfiguredCulture()
    {
        const string markdown = "```mermaid\nflowchart LR\nA-->B\n```";
        var options = MermaidRenderOptions.Default with
        {
            Layout = MermaidLayoutMode.Elk,
            DiagnosticCulture = CultureInfo.GetCultureInfo("fr-FR"),
            StringProvider = new CultureEchoStringProvider(),
        };
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(options)
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        MarkdownDocument first = await ParseUnderCultureAsync(
            engine,
            markdown,
            CultureInfo.GetCultureInfo("en-US"));
        string equalButDistinctMarkdown = new(markdown.ToCharArray());
        Assert.NotSame(markdown, equalButDistinctMarkdown);
        MarkdownDocument second = await ParseUnderCultureAsync(
            engine,
            equalButDistinctMarkdown,
            CultureInfo.GetCultureInfo("de-DE"));

        Assert.Same(first, second);
        Assert.Equal(
            "fr-FR",
            Assert.Single(first.Diagnostics, static value => value.Code == "MMR0002").Message);
    }

    [Fact]
    public async Task ReusableBuilderIsolatedFromAssignedCultureMutation()
    {
        const string markdown = "```mermaid\nflowchart LR\nA-->B\n```";
        var suppliedCulture = new CultureInfo("fr-FR");
        string expectedSeparator = suppliedCulture.NumberFormat.NumberDecimalSeparator;
        var options = MermaidRenderOptions.Default with
        {
            Layout = MermaidLayoutMode.Elk,
            DiagnosticCulture = suppliedCulture,
            StringProvider = new NumberSeparatorStringProvider(),
        };
        var builder = new MarkdownEngineBuilder().UseMermaid(options);

        suppliedCulture.NumberFormat.NumberDecimalSeparator = "mutated";
        using MarkdownEngine firstEngine = builder.Build();
        using MarkdownEngine secondEngine = builder.Build();

        MarkdownDocument first = await firstEngine.ParseAsync(markdown);
        MarkdownDocument second = await secondEngine.ParseAsync(markdown);
        Assert.Equal(
            expectedSeparator,
            Assert.Single(first.Diagnostics, static value => value.Code == "MMR0002").Message);
        Assert.Equal(
            expectedSeparator,
            Assert.Single(second.Diagnostics, static value => value.Code == "MMR0002").Message);
    }

    private static async Task<MarkdownDocument> ParseUnderCultureAsync(
        MarkdownEngine engine,
        string markdown,
        CultureInfo culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            return await engine.ParseAsync(markdown);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed class TestFenceExtension : IMarkdownExtension
    {
        public string Id => "tests.hosted-fence";

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, static (context, content) =>
            {
                if (!context.Node.Attributes.TryGetValue("language", out string? language) ||
                    !string.Equals(language, "hosted", StringComparison.Ordinal))
                {
                    return;
                }

                content.AddHostedElement(
                    "tests.hosted",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.Body,
                    semanticText: context.Node.Literal);
            });
    }

    private sealed class TestMermaidFenceExtension : IMarkdownExtension
    {
        public string Id => "tests.custom-mermaid-fence";

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, static (context, content) =>
            {
                if (context.Node.Attributes.TryGetValue("language", out string? language) &&
                    string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
                {
                    content.AddText(
                        "custom-mermaid",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body);
                }
            });
    }

    private sealed class RendererUnavailableStringProvider : IMermaidStringProvider
    {
        public string? GetString(
            string resourceKey,
            CultureInfo culture,
            IReadOnlyList<object?> arguments) =>
            resourceKey == MermaidStringKeys.RendererUnavailable
                ? "localized-renderer-unavailable"
                : null;
    }

    private sealed class CultureEchoStringProvider : IMermaidStringProvider
    {
        public string? GetString(
            string resourceKey,
            CultureInfo culture,
            IReadOnlyList<object?> arguments) =>
            culture.Name;
    }

    private sealed class NumberSeparatorStringProvider : IMermaidStringProvider
    {
        public string? GetString(
            string resourceKey,
            CultureInfo culture,
            IReadOnlyList<object?> arguments) =>
            culture.NumberFormat.NumberDecimalSeparator;
    }
}
