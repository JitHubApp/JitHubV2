using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

/// <summary>
/// Characterizes authored text that happens to have Mermaid's private entity-placeholder spelling.
/// These cases are intentionally public-engine tests: a decoder-only unit test would not prove
/// whether diagram preprocessing and a family renderer retain the text's origin.
/// </summary>
public sealed class MermaidPlaceholderFamilyIntegrationTests
{
    private const string LiteralPlaceholder = "ﬂ°quot¶ß";
    private const string MermaidEntity = "#quot;";

    [Theory]
    [InlineData(LiteralPlaceholder, LiteralPlaceholder)]
    [InlineData(MermaidEntity, "\"")]
    [InlineData(LiteralPlaceholder + " " + MermaidEntity, LiteralPlaceholder + " \"")]
    [InlineData(MermaidEntity + " " + LiteralPlaceholder + " " + MermaidEntity, "\" " + LiteralPlaceholder + " \"")]
    [InlineData("😀" + LiteralPlaceholder + MermaidEntity + "e\u0301", "😀" + LiteralPlaceholder + "\"e\u0301")]
    [InlineData("left#lt;b#gt;text#lt;/b#gt;right", "left<b>text</b>right")]
    [InlineData("left<b>text</b>right", "lefttextright")]
    public Task SequenceMessagePreservesLiteralPlaceholderOrigin(string sourceText, string expectedText) =>
        AssertFamilyTextAsync(
            $"sequenceDiagram\n  Alice->>Bob: {sourceText}",
            expectedText,
            MarkdownVectorSemanticRole.Diagram,
            "sequence message");

    [Theory]
    [InlineData("participant", 2)]
    [InlineData("actor", 2)]
    [InlineData("participant-yaml", 2)]
    [InlineData("participant-yaml-escaped", 2)]
    [InlineData("box", 1)]
    [InlineData("title", 1)]
    [InlineData("note", 1)]
    [InlineData("loop", 1)]
    [InlineData("opt", 1)]
    [InlineData("alt", 1)]
    [InlineData("par", 1)]
    [InlineData("critical", 1)]
    [InlineData("break", 1)]
    [InlineData("wrapped-message", 1)]
    [InlineData("after-activation", 1)]
    public async Task SequenceVisibleLabelRoutesPreserveExactMixedOrigin(string route, int visibleOccurrences)
    {
        const string labelSource = "😀ﬂ°quot¶ß#quot;e\u0301";
        const string labelText = "😀ﬂ°quot¶ß\"e\u0301";
        string body = route switch
        {
            "participant" => $"participant A as :nowrap: {labelSource}\nA->>B: complete",
            "actor" => $"actor A as :nowrap: {labelSource}\nA->>B: complete",
            "participant-yaml" => $"participant A@{{alias: ':nowrap: {labelSource}'}}\nA->>B: complete",
            "participant-yaml-escaped" =>
                "participant A@{alias: \"😀\\uFB02\\u00B0quot\\u00B6\\u00DF#quot;e\\u0301\"}\nA->>B: complete",
            "box" => $"box rgb(10, 20, 30) :nowrap: {labelSource}\nparticipant A\nend\nA->>B: complete",
            "title" => $"title {labelSource}\nA->>B: complete",
            "note" => $"note over A,B: :nowrap: {labelSource}\nA->>B: complete",
            "loop" or "opt" or "alt" or "par" or "critical" or "break" =>
                $"{route} :nowrap: {labelSource}\nA->>B: complete\nend",
            "wrapped-message" => $"A->>B: :wrap: {labelSource}",
            "after-activation" => $"autonumber\nA->>+B: start\nnote over A,B: note\nB-->>A: {labelSource}\ndeactivate B",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\nsequenceDiagram\n{body}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        string expected = route is "loop" or "opt" or "alt" or "par" or "critical" or "break"
            ? $"[{labelText}]"
            : labelText;
        MarkdownVectorCommand[] labels = scene.Commands.Where(command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            string.Equals(command.Text, expected, StringComparison.Ordinal)).ToArray();
        Assert.True(labels.Length == visibleOccurrences,
            $"{route}: expected {visibleOccurrences} exact labels {Format(expected)}, observed " +
            string.Join(" | ", scene.Commands.Where(command => command.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(command => Format(command.Text!))));
        foreach (MarkdownVectorCommand label in labels)
            Assert.InRange(label.SemanticIndex, 0, scene.Semantics.Count - 1);
        Assert.Equal(visibleOccurrences, CountOccurrences(Assert.IsType<string>(content.SemanticText), expected));
    }

    [Theory]
    [InlineData(LiteralPlaceholder, LiteralPlaceholder)]
    [InlineData(MermaidEntity, "\"")]
    [InlineData(LiteralPlaceholder + " " + MermaidEntity, LiteralPlaceholder + " \"")]
    [InlineData(MermaidEntity + " " + LiteralPlaceholder + " " + MermaidEntity, "\" " + LiteralPlaceholder + " \"")]
    [InlineData("😀" + LiteralPlaceholder + MermaidEntity + "e\u0301", "😀" + LiteralPlaceholder + "\"e\u0301")]
    public Task StateEdgeLabelPreservesLiteralPlaceholderOrigin(string sourceText, string expectedText) =>
        AssertFamilyTextAsync(
            $"stateDiagram-v2\n  [*] --> Ready : {sourceText}",
            expectedText,
            MarkdownVectorSemanticRole.Edge,
            "state edge label");

    [Theory]
    [InlineData(LiteralPlaceholder, LiteralPlaceholder)]
    [InlineData(MermaidEntity, "\"")]
    [InlineData(LiteralPlaceholder + " " + MermaidEntity, LiteralPlaceholder + " \"")]
    [InlineData(MermaidEntity + " " + LiteralPlaceholder + " " + MermaidEntity, "\" " + LiteralPlaceholder + " \"")]
    [InlineData("😀" + LiteralPlaceholder + MermaidEntity + "e\u0301", "😀" + LiteralPlaceholder + "\"e\u0301")]
    [InlineData("literal #amp;quot;", "literal &quot;")]
    [InlineData("literal #lt;b#gt;", "literal <b>")]
    [InlineData("literal #92;n", "literal \\n")]
    public Task MindmapNodePreservesLiteralPlaceholderOrigin(string sourceText, string expectedText) =>
        AssertFamilyTextAsync(
            $"mindmap\n  root[{sourceText}]\n    child",
            expectedText,
            MarkdownVectorSemanticRole.Node,
            "mindmap root label");

    [Theory]
    [InlineData(LiteralPlaceholder, LiteralPlaceholder)]
    [InlineData(MermaidEntity, "\"")]
    [InlineData(LiteralPlaceholder + " " + MermaidEntity, LiteralPlaceholder + " \"")]
    [InlineData(MermaidEntity + " " + LiteralPlaceholder + " " + MermaidEntity, "\" " + LiteralPlaceholder + " \"")]
    [InlineData("😀" + LiteralPlaceholder + MermaidEntity + "e\u0301", "😀" + LiteralPlaceholder + "\"e\u0301")]
    public Task ClassRelationshipLabelPreservesLiteralPlaceholderOrigin(string sourceText, string expectedText) =>
        AssertFamilyTextAsync(
            $"classDiagram\n  class A\n  class B\n  A --> B : {sourceText}",
            expectedText,
            MarkdownVectorSemanticRole.Edge,
            "class relationship label");

    [Theory]
    [InlineData("sequence")]
    [InlineData("state")]
    [InlineData("mindmap")]
    [InlineData("class")]
    public async Task ExactOriginLabelGeometryMatchesIndependentScalarEscapes(string family)
    {
        const string expected = LiteralPlaceholder + "\"";
        const string independentSource = "#64258;#176;quot#182;#223;#quot;";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownVectorScene reference = await ParseAsync(independentSource);
        MarkdownVectorScene mixed = await ParseAsync(LiteralPlaceholder + MermaidEntity);
        Assert.Equal(reference.Viewport, mixed.Viewport);
        Assert.Equal(
            Assert.Single(reference.Commands, command => command.Kind == MarkdownVectorCommandKind.DrawText && command.Text == expected).Rectangle,
            Assert.Single(mixed.Commands, command => command.Kind == MarkdownVectorCommandKind.DrawText && command.Text == expected).Rectangle);
        Assert.Equal(reference.Semantics.Select(static item => item.Bounds), mixed.Semantics.Select(static item => item.Bounds));

        async Task<MarkdownVectorScene> ParseAsync(string label)
        {
            string body = family switch
            {
                "sequence" => $"sequenceDiagram\nA->>B: {label}",
                "state" => $"stateDiagram-v2\n[*] --> Ready : {label}",
                "mindmap" => $"mindmap\n  root[{label}]\n    child",
                "class" => $"classDiagram\nclass A\nclass B\nA --> B : {label}",
                _ => throw new ArgumentOutOfRangeException(nameof(family)),
            };
            MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{body}\n```\n");
            Assert.Empty(document.Diagnostics);
            MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
            Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
            MarkdownContent content = Assert.Single(fragment!.Items);
            MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
            Assert.Single(scene.Commands, command => command.Kind == MarkdownVectorCommandKind.DrawText && command.Text == expected);
            Assert.Equal(1, CountOccurrences(Assert.IsType<string>(content.SemanticText), expected));
            return scene;
        }
    }

    private static async Task AssertFamilyTextAsync(
        string source,
        string expectedText,
        MarkdownVectorSemanticRole expectedRole,
        string description)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```\n");

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);

        // Wrapping and shaping may split one logical caption into multiple paint commands.
        // Match the complete text of one owner, never fragments from unrelated nodes/edges.
        var matchingOwners = scene.Commands
            .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
            .GroupBy(static command => command.SemanticIndex)
            .Where(owner => string.Equals(
                string.Concat(owner.Select(static command => command.Text)),
                expectedText,
                StringComparison.Ordinal))
            .ToArray();
        string observedDrawText = string.Join(
            " | ",
            scene.Commands
                .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static command => command.Text));
        string semanticText = Assert.IsType<string>(content.SemanticText);
        Assert.True(
            matchingOwners.Length == 1,
            $"{description} expected one owner painting exactly {Format(expectedText)}; " +
            $"observed DrawText=[{observedDrawText}], SemanticText={Format(semanticText)}");

        MarkdownVectorCommand target = matchingOwners[0].First();
        Assert.InRange(target.SemanticIndex, 0, scene.Semantics.Count - 1);
        Assert.Equal(expectedRole, scene.Semantics[target.SemanticIndex].Role);
        if (expectedRole is MarkdownVectorSemanticRole.Node or MarkdownVectorSemanticRole.Edge)
            Assert.Equal(expectedText, scene.Semantics[target.SemanticIndex].Name);

        // Check the full DrawText owned by the selected semantic item, not a diagram-wide text
        // search that could accidentally pass because another node contains the same substring.
        string ownerDrawText = string.Concat(scene.Commands
            .Where(command => command.SemanticIndex == target.SemanticIndex &&
                command.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static command => command.Text));
        Assert.Equal(expectedText, ownerDrawText);

        Assert.Equal(1, CountOccurrences(semanticText, expectedText));
    }

    private static string Format(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

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
