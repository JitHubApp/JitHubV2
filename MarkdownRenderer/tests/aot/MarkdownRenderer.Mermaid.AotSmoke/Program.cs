using MarkdownRenderer.Mermaid;

MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();
if (!runtime.IsAvailable)
{
    Console.Error.WriteLine("Mermaid deployment smoke failed: the packaged native engine is unavailable.");
    return 1;
}

using var renderer = new MermaidRenderer();
MermaidRenderResult result = await renderer.RenderAsync("flowchart LR\nA-->B");
if (result.Status != MermaidRenderStatus.Success ||
    result.ShouldUseFallback ||
    result.Scene is not { Commands.Count: > 0 } scene ||
    result.Diagnostics.Any(diagnostic => diagnostic.Severity == MermaidDiagnosticSeverity.Error))
{
    Console.Error.WriteLine($"Mermaid deployment smoke failed: {result.Status}; no successful scene.");
    return 2;
}

Console.WriteLine($"Mermaid deployment smoke passed: {scene.Commands.Count} vector commands.");
return 0;
