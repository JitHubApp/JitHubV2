using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Everything one rebuild lays out with: its captured inputs, the parsed
/// document, the composed registries, the theme snapshot and the layout
/// context that owns the build's retirements.
/// </summary>
internal sealed class RebuildLayoutPlan
{
    internal required RebuildSourceInputs Source { get; init; }
    internal required RebuildLayoutInputs Inputs { get; init; }
    internal required RebuildParseResult Parse { get; init; }
    internal required float Width { get; init; }

    /// <summary>The presentation registry the document or engine carries, or the default.</summary>
    internal required MarkdownExtensionRegistry BaseRegistry { get; init; }

    internal required int PresentationRegistryRevision { get; init; }
    internal required int ExtensionRegistryRevision { get; init; }

    /// <summary>The base registry composed with the host's extension registry.</summary>
    internal required MarkdownExtensionRegistry LayoutRegistry { get; init; }

    /// <summary>The resolver layout uses: the host's, or a performance document scope around it.</summary>
    internal required IMarkdownImageResolver? ImageResolver { get; init; }

    /// <summary>The performance session, or null when it declined a document scope.</summary>
    internal required IMarkdownPerformanceSessionInternal? PerformanceSession { get; init; }

    internal required ThemeSnapshot Theme { get; init; }
    internal required double ThemeSnapshotMilliseconds { get; init; }
    internal required CanvasDevice Device { get; init; }
    internal required MarkdownLayoutContext Context { get; init; }
    internal required MarkdownLayoutDiagnostics? LayoutDiagnostics { get; init; }

    /// <summary>The identity that decides whether automation sees the same logical document.</summary>
    internal MarkdownAutomationDocumentIdentity CreateAutomationDocumentIdentity(LayoutSnapshot snapshot) =>
        new(
            snapshot.SourceFingerprint,
            snapshot.SemanticDocument.TextProjectionFingerprint,
            snapshot.SemanticDocument.SemanticRangeFingerprint,
            Source.ReusableDocument,
            Source.Engine,
            Source.LegacyRegistry,
            Source.ParserRegistryRevision,
            BaseRegistry,
            PresentationRegistryRevision,
            Source.ExtensionRegistry,
            ExtensionRegistryRevision,
            Inputs.StringProvider,
            Inputs.Language);
}
