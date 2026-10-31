using MarkdownRenderer;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Mermaid;

MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();
if (!runtime.IsAvailable)
{
    Console.Error.WriteLine("Mermaid deployment smoke failed: the packaged native engine is unavailable.");
    return 1;
}

// Render through the public host path: an engine with UseMermaid parses the
// fence and stores the diagram's vector scene as the block's extension content.
const string markdown = "```mermaid\nflowchart LR\nA-->B\n```\n";
using MarkdownEngine engine = new MarkdownEngineBuilder()
    .UseMermaid()
    .Build();
MarkdownDocument document = await engine.ParseAsync(markdown);

MarkdownCodeBlock fence = document.GetCodeBlocks().Single();
if (document.HasErrors ||
    !document.TryGetBlockExtensionContent(fence.SourceSpan, out MarkdownContentFragment? fragment) ||
    fragment!.Items is not [{ Kind: MarkdownContentKind.VectorScene, VectorScene: { Commands.Count: > 0 } scene }])
{
    string diagnostics = string.Join("; ", document.Diagnostics.Select(diagnostic => diagnostic.Code));
    Console.Error.WriteLine($"Mermaid deployment smoke failed: no diagram scene ({diagnostics}).");
    return 2;
}

Console.WriteLine($"Mermaid deployment smoke passed: {scene.Commands.Count} vector commands.");
return 0;
