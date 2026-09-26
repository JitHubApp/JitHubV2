using MarkdownRenderer.Mermaid;

MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();
using var renderer = new MermaidRenderer();
MermaidRenderResult result = await renderer.RenderAsync("flowchart LR\nA-->B");
return runtime.IsAvailable || result.Status == MermaidRenderStatus.EngineUnavailable ? 0 : 1;
