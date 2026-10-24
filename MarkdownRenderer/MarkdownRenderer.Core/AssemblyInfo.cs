using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MarkdownRenderer")]
[assembly: InternalsVisibleTo("MarkdownRenderer.GitHub.Tests")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Tests")]
[assembly: InternalsVisibleTo("MarkdownRenderer.PixelTests")]
[assembly: InternalsVisibleTo("MarkdownRenderer.PerformanceHarness")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Performance")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Gfm")]
[assembly: InternalsVisibleTo("MarkdownRenderer.GitHub")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Html")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Math")]
[assembly: InternalsVisibleTo("MarkdownRenderer.Mermaid")]
// The README correctness audit inventories the parsed tree the native view lays out.
[assembly: InternalsVisibleTo("MarkdownRenderer.ReadmeCorrectnessAudit")]
