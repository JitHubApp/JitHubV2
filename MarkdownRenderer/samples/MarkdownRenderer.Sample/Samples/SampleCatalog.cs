using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Sample;

/// <summary>
/// The sample pages a window offers. The catalog owns the generated pages, so
/// each large document is built once per window, on first navigation.
/// </summary>
internal sealed class SampleCatalog
{
    internal SampleCatalog()
    {
        var virtualization = new Lazy<string>(DiagnosticsSampleDocuments.CreateVirtualizationSample);
        var stress = new Lazy<string>(DiagnosticsSampleDocuments.CreateStressSample);
        var progressiveScenes = new Lazy<string>(DiagnosticsSampleDocuments.CreateProgressiveScenesSample);
        Groups = CreateGroups(virtualization, stress, progressiveScenes);
    }

    internal IReadOnlyList<SampleGroup> Groups { get; }

    // Stable keys drive navigation and automation. Display strings are resolved
    // independently so localization can never change page identity.
    private static IReadOnlyList<SampleGroup> CreateGroups(
        Lazy<string> virtualization,
        Lazy<string> stress,
        Lazy<string> progressiveScenes) =>
    [
        new("GroupOverview", "Overview",
        [
            new("FullDemo", "PageFullDemo", "Full demo", "\uE80F", static () => OverviewSampleDocuments.FullDemoSample),
        ]),
        new("GroupMarkdown", "Markdown",
        [
            new("Typography", "PageTypography", "Typography", "\uE8D2", static () => MarkdownSampleDocuments.TypographySample),
            new("Lists", "PageLists", "Lists", "\uEA37", static () => MarkdownSampleDocuments.ListsSample),
            new("Tables", "PageTables", "Tables", "\uE80A", static () => MarkdownSampleDocuments.TablesSample),
            new("Code", "PageCode", "Code and TextMate", "\uE943", static () => MarkdownSampleDocuments.CodeSample),
            new("Images", "PageImages", "Images and SVG", "\uEB9F", static () => MarkdownSampleDocuments.ImagesSample),
        ]),
        new("GroupProfiles", "Profiles",
        [
            new("GitHubAlerts", "PageGitHubAlerts", "GitHub alerts", "\uE946", static () => ProfileSampleDocuments.AlertsSample),
            new("Footnotes", "PageFootnotes", "Footnotes", "\uE8A5", static () => ProfileSampleDocuments.FootnotesSample),
            new("MarkdownExtra", "PageMarkdownExtra", "Markdown Extra", "\uE70B", static () => ProfileSampleDocuments.MarkdownExtraSample),
            new("Html", "PageHtml", "Safe HTML", "\uE774", static () => ProfileSampleDocuments.HtmlSample),
        ]),
        new("GroupFeaturePacks", "Feature packs",
        [
            new("Math", "PageMath", "Math", "\uE8EF", static () => FeaturePackSampleDocuments.MathSample),
            new("Mermaid", "PageMermaid", "Mermaid", "\uE8A0", static () => FeaturePackSampleDocuments.MermaidSample),
            new("SvgStress", "PageSvgStress", "SVG rendering", "\uEB9F", static () => SvgSampleDocuments.SvgStressSample),
            new("Diagrams", "PageDiagrams", "Diagram pipeline", "\uE9D2", static () => FeaturePackSampleDocuments.DiagramEmbedSample),
            new("Embeds", "PageEmbeds", "Hosted elements", "\uEDE3", static () => FeaturePackSampleDocuments.EmbedsSample),
        ]),
        new("GroupInteraction", "Interaction",
        [
            new("Selection", "PageSelection", "Selection and copy", "\uE8B0", static () => InteractionSampleDocuments.SelectionSample),
            new("TouchSelection", "PageTouchSelection", "Touch selection", "\uE815", static () => InteractionSampleDocuments.TouchSelectionSample),
            new("KeyboardNav", "PageKeyboardNav", "Keyboard navigation", "\uE765", static () => InteractionSampleDocuments.KeyboardNavSample),
            new("Rtl", "PageRtl", "Right-to-left", "\uE8C0", static () => InteractionSampleDocuments.RtlSample),
            new("LazyImages", "PageLazyImages", "Lazy images", "\uE8B9", static () => InteractionSampleDocuments.LazyImagesSample),
            new("ScrollAnchor", "PageScrollAnchor", "Scroll anchoring", "\uE8CB", static () => InteractionSampleDocuments.ScrollAnchorSample),
        ]),
        new("GroupDiagnostics", "Diagnostics and scale",
        [
            new("Virtualization", "PageVirtualization", "Virtualization", "\uE950", () => virtualization.Value),
            new("Stress", "PageStress", "Stress document", "\uE7BA", () => stress.Value),
            new("ProgressiveScenes", "PageProgressiveScenes", "Progressive scenes", "\uE950", () => progressiveScenes.Value),
            new("AccessibilityLab", "PageAccessibilityLab", "Accessibility lab", "\uE776", static () => DiagnosticsSampleDocuments.AccessibilityLabSample),
            new("AuditMatrix", "PageAuditMatrix", "Audit matrix", "\uE9D9", static () => DiagnosticsSampleDocuments.AuditMatrixSample),
            new("Providers", "PageProviders", "Provider configuration", "\uE912", static () => ProviderSampleDocuments.ProvidersSample),
        ]),
    ];
}
