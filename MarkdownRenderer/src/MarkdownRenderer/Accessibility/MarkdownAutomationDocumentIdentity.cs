using System;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The inputs that change the semantic document exposed by TextPattern. Source,
/// semantic text, and its ordered range projection are fingerprinted off-thread;
/// layout width, theme, and snapshot identity are excluded so ranges remain live
/// through ordinary relayout and restyle commits.
/// </summary>
internal sealed class MarkdownAutomationDocumentIdentity
{
    internal MarkdownAutomationDocumentIdentity(
        MarkdownAutomationTextFingerprint sourceFingerprint,
        MarkdownAutomationTextFingerprint semanticTextFingerprint,
        MarkdownAutomationTextFingerprint semanticRangeFingerprint,
        object? explicitDocument,
        object? engine,
        object? parserRegistry,
        int parserRegistryRevision,
        object? presentationRegistry,
        int presentationRegistryRevision,
        object? extensionRegistry,
        int extensionRegistryRevision,
        object? stringProvider,
        string cultureName)
    {
        SourceFingerprint = sourceFingerprint;
        SemanticTextFingerprint = semanticTextFingerprint;
        SemanticRangeFingerprint = semanticRangeFingerprint;
        ExplicitDocument = explicitDocument;
        Engine = engine;
        ParserRegistry = parserRegistry;
        ParserRegistryRevision = parserRegistryRevision;
        PresentationRegistry = presentationRegistry;
        PresentationRegistryRevision = presentationRegistryRevision;
        ExtensionRegistry = extensionRegistry;
        ExtensionRegistryRevision = extensionRegistryRevision;
        StringProvider = stringProvider;
        CultureName = cultureName;
    }

    private readonly MarkdownAutomationTextFingerprint SourceFingerprint;
    private readonly MarkdownAutomationTextFingerprint SemanticTextFingerprint;
    private readonly MarkdownAutomationTextFingerprint SemanticRangeFingerprint;
    private readonly object? ExplicitDocument;
    private readonly object? Engine;
    private readonly object? ParserRegistry;
    private readonly int ParserRegistryRevision;
    private readonly object? PresentationRegistry;
    private readonly int PresentationRegistryRevision;
    private readonly object? ExtensionRegistry;
    private readonly int ExtensionRegistryRevision;
    private readonly object? StringProvider;
    private readonly string CultureName;

    internal bool IsEquivalentTo(MarkdownAutomationDocumentIdentity other) =>
        ReferenceEquals(ExplicitDocument, other.ExplicitDocument) &&
        ReferenceEquals(Engine, other.Engine) &&
        ReferenceEquals(ParserRegistry, other.ParserRegistry) &&
        ParserRegistryRevision == other.ParserRegistryRevision &&
        ReferenceEquals(PresentationRegistry, other.PresentationRegistry) &&
        PresentationRegistryRevision == other.PresentationRegistryRevision &&
        ReferenceEquals(ExtensionRegistry, other.ExtensionRegistry) &&
        ExtensionRegistryRevision == other.ExtensionRegistryRevision &&
        ReferenceEquals(StringProvider, other.StringProvider) &&
        SourceFingerprint.Equals(other.SourceFingerprint) &&
        SemanticTextFingerprint.Equals(other.SemanticTextFingerprint) &&
        SemanticRangeFingerprint.Equals(other.SemanticRangeFingerprint) &&
        string.Equals(CultureName, other.CultureName, StringComparison.OrdinalIgnoreCase);
}
