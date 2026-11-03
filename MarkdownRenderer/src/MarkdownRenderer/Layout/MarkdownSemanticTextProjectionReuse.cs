using System;
using MarkdownRenderer.Document;
using MarkdownRenderer.Accessibility;

namespace MarkdownRenderer.Layout;

// Short-lived, value-only handoff for immutable text projections. This must
// never grow to include a LayoutSnapshot, semantic node/span, box, run, or
// control reference: candidate construction occurs after the source read gate.
internal readonly struct MarkdownSemanticTextProjectionReuse
{
    internal readonly MarkdownSourceMap SourceMap;
    internal readonly MarkdownAutomationTextFingerprint SourceFingerprint;
    internal readonly string Text;
    internal readonly TextElementBoundaryIndex TextElementBoundaries;
    internal readonly MarkdownAutomationTextFingerprint TextProjectionFingerprint;

    private MarkdownSemanticTextProjectionReuse(
        MarkdownSourceMap sourceMap,
        MarkdownAutomationTextFingerprint sourceFingerprint,
        string text,
        TextElementBoundaryIndex textElementBoundaries,
        MarkdownAutomationTextFingerprint textProjectionFingerprint)
    {
        SourceMap = sourceMap;
        SourceFingerprint = sourceFingerprint;
        Text = text;
        TextElementBoundaries = textElementBoundaries;
        TextProjectionFingerprint = textProjectionFingerprint;
    }

    internal static MarkdownSemanticTextProjectionReuse Capture(LayoutSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MarkdownSemanticDocument semantic = source.SemanticDocument;
        return new MarkdownSemanticTextProjectionReuse(
            source.SourceMap,
            source.SourceFingerprint,
            semantic.Text,
            semantic.TextElementBoundaries,
            semantic.TextProjectionFingerprint);
    }
}
