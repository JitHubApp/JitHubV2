using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Sample;

internal sealed record SampleDefinition(
    string Key,
    string TitleResourceKey,
    string FallbackTitle,
    string IconGlyph,
    Func<string> SourceFactory);

internal sealed record SampleGroup(
    string TitleResourceKey,
    string FallbackTitle,
    IReadOnlyList<SampleDefinition> Pages);

internal static class SampleCatalog
{
    // Stable keys drive navigation and automation. Display strings are resolved
    // independently so localization can never change page identity.
    internal static IReadOnlyList<SampleGroup> Groups { get; } =
    [
        new("GroupOverview", "Overview",
        [
            new("FullDemo", "PageFullDemo", "Full demo", "\uE80F", static () => SampleDocuments.FullDemoSample),
        ]),
        new("GroupMarkdown", "Markdown",
        [
            new("Typography", "PageTypography", "Typography", "\uE8D2", static () => SampleDocuments.TypographySample),
            new("Lists", "PageLists", "Lists", "\uEA37", static () => SampleDocuments.ListsSample),
            new("Tables", "PageTables", "Tables", "\uE80A", static () => SampleDocuments.TablesSample),
            new("Code", "PageCode", "Code and TextMate", "\uE943", static () => SampleDocuments.CodeSample),
            new("Images", "PageImages", "Images and SVG", "\uEB9F", static () => SampleDocuments.ImagesSample),
        ]),
        new("GroupProfiles", "Profiles",
        [
            new("GitHubAlerts", "PageGitHubAlerts", "GitHub alerts", "\uE946", static () => SampleDocuments.AlertsSample),
            new("Footnotes", "PageFootnotes", "Footnotes", "\uE8A5", static () => SampleDocuments.FootnotesSample),
            new("MarkdownExtra", "PageMarkdownExtra", "Markdown Extra", "\uE70B", static () => SampleDocuments.MarkdownExtraSample),
            new("Html", "PageHtml", "Safe HTML", "\uE774", static () => SampleDocuments.HtmlSample),
        ]),
        new("GroupFeaturePacks", "Feature packs",
        [
            new("Math", "PageMath", "Math", "\uE8EF", static () => SampleDocuments.MathSample),
            new("Mermaid", "PageMermaid", "Mermaid", "\uE8A0", static () => SampleDocuments.MermaidSample),
            new("Diagrams", "PageDiagrams", "Diagram pipeline", "\uE9D2", static () => SampleDocuments.DiagramEmbedSample),
            new("Embeds", "PageEmbeds", "Hosted elements", "\uEDE3", static () => SampleDocuments.EmbedsSample),
        ]),
        new("GroupInteraction", "Interaction",
        [
            new("Selection", "PageSelection", "Selection and copy", "\uE8B0", static () => SampleDocuments.SelectionSample),
            new("TouchSelection", "PageTouchSelection", "Touch selection", "\uE815", static () => SampleDocuments.TouchSelectionSample),
            new("KeyboardNav", "PageKeyboardNav", "Keyboard navigation", "\uE765", static () => SampleDocuments.KeyboardNavSample),
            new("Rtl", "PageRtl", "Right-to-left", "\uE8C0", static () => SampleDocuments.RtlSample),
            new("LazyImages", "PageLazyImages", "Lazy images", "\uE8B9", static () => SampleDocuments.LazyImagesSample),
            new("ScrollAnchor", "PageScrollAnchor", "Scroll anchoring", "\uE8CB", static () => SampleDocuments.ScrollAnchorSample),
        ]),
        new("GroupDiagnostics", "Diagnostics and scale",
        [
            new("Virtualization", "PageVirtualization", "Virtualization", "\uE950", static () => SampleDocuments.VirtualizationSample),
            new("Stress", "PageStress", "Stress document", "\uE7BA", static () => SampleDocuments.StressSample),
            new("AccessibilityLab", "PageAccessibilityLab", "Accessibility lab", "\uE776", static () => SampleDocuments.AccessibilityLabSample),
            new("AuditMatrix", "PageAuditMatrix", "Audit matrix", "\uE9D9", static () => SampleDocuments.AuditMatrixSample),
        ]),
    ];
}
