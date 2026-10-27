using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidSemanticTextIntegrationTests
{
    [Theory]
    [InlineData("#quot;", "\"")]
    [InlineData("ﬂ°quot¶ß", "ﬂ°quot¶ß")]
    [InlineData("ﬂ°quot¶ß #quot;", "ﬂ°quot¶ß \"")]
    public async Task SequenceAccessibilityMetadataPreservesFinalLiteralContent(string sourceText, string expectedText)
    {
        string source = $"sequenceDiagram\naccTitle: title {sourceText}\naccDescr: description {sourceText}\nA->>B: body";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        MarkdownVectorSemanticItem diagram = Assert.Single(scene.Semantics,
            static item => item.Role == MarkdownVectorSemanticRole.Diagram);
        Assert.Equal($"title {expectedText}", diagram.Name);
        Assert.Equal($"description {expectedText}", diagram.Description);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        Assert.DoesNotContain("title ", semanticText, StringComparison.Ordinal);
        Assert.DoesNotContain("description ", semanticText, StringComparison.Ordinal);
        Assert.Contains(scene.Commands, static command => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == "body");
    }

    [Fact]
    public async Task SequenceHiddenActorLinkMenuIsNotPaintedOrCopiedAsDocumentBody()
    {
        const string hiddenCaption = "hidden-menu-caption-sentinel";
        const string source = "sequenceDiagram\n" +
            "participant A as Alice\n" +
            "participant B as Bob\n" +
            "links A: {\"hidden-menu-caption-sentinel\": \"https://example.test/docs\"}\n" +
            "A->>B: Visible message";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.DoesNotContain(scene.Commands, command => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == hiddenCaption);
        Assert.DoesNotContain(hiddenCaption, Assert.IsType<string>(content.SemanticText), StringComparison.Ordinal);
        Assert.Contains(scene.Commands, static command => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == "Visible message");
    }

    [Theory]
    [InlineData("uses stable text")]
    [InlineData("مرحبا API 👩‍💻")]
    public async Task ClassRelationshipCaptionRemainsOneLogicalLabelForCopy(string caption)
    {
        string source = $"classDiagram\nclass A\nclass B\nA --> B : {caption}";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        MarkdownVectorSemanticItem relation = Assert.Single(scene.Semantics,
            static item => item.Role == MarkdownVectorSemanticRole.Edge);
        Assert.Equal(caption, relation.Name);
        int relationIndex = Enumerable.Range(0, scene.Semantics.Count)
            .Single(index => scene.Semantics[index].Role == MarkdownVectorSemanticRole.Edge);
        MarkdownVectorCommand[] relationText = scene.Commands.Where(command =>
            command.Kind == MarkdownVectorCommandKind.DrawText && command.SemanticIndex == relationIndex).ToArray();
        Assert.NotEmpty(relationText);
        Assert.Equal(caption, string.Concat(relationText.Select(static command => command.Text)));
        Assert.Contains(scene.Commands, command => command.Kind == MarkdownVectorCommandKind.DrawPath &&
            command.SemanticIndex == relationIndex);

        // Separate SVG tspans are shaping fragments, not separate logical lines in copied text.
        string semanticText = Assert.IsType<string>(content.SemanticText);
        Assert.Contains(caption, semanticText.Split(Environment.NewLine, StringSplitOptions.None));
        Assert.Equal(1, CountOccurrences(semanticText, caption));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateTransitionCaptionsSharePathOwnershipAcrossCompositeBoundaries(bool composite)
    {
        const string mixedCaption = "مرحبا API 👩‍💻";
        const string finalCaption = "finish transition";
        string states = $"[*] --> Ready\nReady --> Working : {mixedCaption}\n";
        string source = "stateDiagram-v2\n" +
            (composite ? $"state Active {{\n{states}}}\n" : states) +
            $"Working --> [*] : {finalCaption}";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        int[] edges = Enumerable.Range(0, scene.Semantics.Count)
            .Where(index => scene.Semantics[index].Role == MarkdownVectorSemanticRole.Edge).ToArray();
        Assert.Equal(3, edges.Length);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        foreach (string caption in new[] { mixedCaption, finalCaption })
        {
            int owner = Assert.Single(edges, index => scene.Semantics[index].Name == caption);
            MarkdownVectorCommand[] text = scene.Commands.Where(command =>
                command.SemanticIndex == owner && command.Kind == MarkdownVectorCommandKind.DrawText).ToArray();
            Assert.NotEmpty(text);
            Assert.Equal(caption, string.Concat(text.Select(static command => command.Text)));
            Assert.Contains(scene.Commands, command => command.SemanticIndex == owner &&
                command.Kind == MarkdownVectorCommandKind.DrawPath);
            Assert.Contains(caption, semanticText.Split(Environment.NewLine, StringSplitOptions.None));
            Assert.Equal(1, CountOccurrences(semanticText, caption));
        }
        int unnamedEdge = Assert.Single(edges, index => string.IsNullOrEmpty(scene.Semantics[index].Name));
        Assert.DoesNotContain(scene.Commands, command => command.SemanticIndex == unnamedEdge &&
            command.Kind == MarkdownVectorCommandKind.DrawText);
    }

    [Theory]
    [InlineData("مرحبا API 👩‍💻")]
    [InlineData("Same label")]
    public async Task DistinctNodesWithTheSameLabelEachAppearOnceInAtomicSemanticText(string label)
    {
        string source = $"flowchart LR\nfirst[\"{label}\"] --> second[\"{label}\"]";
        string markdown = $"```mermaid\n{source}\n```\n";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            block.SourceSpan,
            out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        MarkdownVectorSemanticItem[] matchingNodes = scene.Semantics
            .Where(item => item.Role == MarkdownVectorSemanticRole.Node &&
                string.Equals(item.Name, label, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, matchingNodes.Length);
        Assert.Equal($"{label}{Environment.NewLine}{label}", semanticText);
    }

    [Fact]
    public async Task SequenceAccessibleTitleDoesNotReplaceRootOwnedRenderedText()
    {
        const string repeatedText = "repeat";
        const string source = "sequenceDiagram\n" +
            "  accTitle: repeat\n" +
            "  title repeat\n" +
            "  participant A as Alice\n" +
            "  participant B as Bob\n" +
            "  Note over A,B: repeat\n" +
            "  A->>B: repeat";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```");

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            block.SourceSpan,
            out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        MarkdownVectorSemanticItem[] semanticItems = scene.Semantics.ToArray();
        int rootIndex = Array.FindIndex(
            semanticItems,
            static item => item.Role == MarkdownVectorSemanticRole.Diagram);

        Assert.True(rootIndex >= 0);
        Assert.Equal(repeatedText, semanticItems[rootIndex].Name);

        MarkdownVectorCommand[] rootText = scene.Commands
            .Where(command => command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.SemanticIndex == rootIndex)
            .ToArray();
        Assert.Equal(3, rootText.Length);
        Assert.All(rootText, command => Assert.Equal(repeatedText, command.Text));

        // The SVG title is the accessible name. The title, note, and message
        // are three separate visible text commands owned by the root.
        Assert.Equal(3, CountOccurrences(semanticText, repeatedText));
        MarkdownVectorCommand[] actorText = scene.Commands
            .Where(command => command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.Text is "Alice" or "Bob")
            .ToArray();
        int[] actorSemanticIndexes = semanticItems
            .Select((item, index) => (item, index))
            .Where(static pair => pair.item.Role == MarkdownVectorSemanticRole.Node &&
                pair.item.Name is "Alice" or "Bob")
            .Select(static pair => pair.index)
            .ToArray();

        // Merman exposes shape-only semantics alongside text-owning semantics
        // for each participant occurrence. Sequence diagrams draw each actor
        // at the top and bottom, so rendered copy includes two labels each;
        // all eight semantic records remain available for accessibility.
        Assert.Equal(8, actorSemanticIndexes.Length);
        Assert.Equal(4, actorSemanticIndexes.Count(index => scene.Commands.Any(
            command => command.SemanticIndex == index && command.Kind == MarkdownVectorCommandKind.DrawText)));
        Assert.Equal(4, actorText.Length);
        Assert.Equal(2, actorText.Count(static command => command.Text == "Alice"));
        Assert.Equal(2, actorText.Count(static command => command.Text == "Bob"));
        Assert.Equal(2, CountOccurrences(semanticText, "Alice"));
        Assert.Equal(2, CountOccurrences(semanticText, "Bob"));
    }

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }
}
