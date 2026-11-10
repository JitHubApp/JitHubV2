using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>
/// What a rebuild parses: the source text and the parser configuration,
/// captured on the UI thread before the parse starts.
/// </summary>
/// <param name="ExtensionRegistry">The host's extension registry, if any.</param>
/// <param name="LegacyRegistry">The registry the legacy parser uses.</param>
/// <param name="ParserRegistryRevision">The legacy registry's revision at capture.</param>
/// <param name="Engine">The engine that parses, if any.</param>
/// <param name="PerformanceSession">The live performance session, if any.</param>
/// <param name="ReusableDocument">The host-assigned document, if any.</param>
/// <param name="Source">The markdown source text.</param>
internal sealed record RebuildSourceInputs(
    MarkdownExtensionRegistry? ExtensionRegistry,
    MarkdownExtensionRegistry LegacyRegistry,
    int ParserRegistryRevision,
    MarkdownEngine? Engine,
    IMarkdownPerformanceSessionInternal? PerformanceSession,
    MarkdownRenderer.Document.MarkdownDocument? ReusableDocument,
    string Source);
