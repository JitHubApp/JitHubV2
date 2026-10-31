using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Automation;

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

/// <summary>
/// Fixed-size identity for an immutable source or semantic-text projection.
/// Created with the background snapshot; UI publication compares only scalars.
/// </summary>
internal readonly struct MarkdownAutomationTextFingerprint : IEquatable<MarkdownAutomationTextFingerprint>
{
    private readonly ulong _word0;
    private readonly ulong _word1;
    private readonly ulong _word2;
    private readonly ulong _word3;

    private MarkdownAutomationTextFingerprint(int length, ulong word0, ulong word1, ulong word2, ulong word3)
    {
        Length = length;
        _word0 = word0;
        _word1 = word1;
        _word2 = word2;
        _word3 = word3;
    }

    internal readonly int Length;

    internal static MarkdownAutomationTextFingerprint Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()), digest);
        return FromSha256(text.Length, digest);
    }

    internal static MarkdownAutomationTextFingerprint FromSha256(int length, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32)
            throw new ArgumentException("A SHA-256 digest must contain 32 bytes.", nameof(digest));

        return new MarkdownAutomationTextFingerprint(
            length,
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }

    public bool Equals(MarkdownAutomationTextFingerprint other) =>
        Length == other.Length &&
        _word0 == other._word0 &&
        _word1 == other._word1 &&
        _word2 == other._word2 &&
        _word3 == other._word3;

    public override bool Equals(object? obj) =>
        obj is MarkdownAutomationTextFingerprint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Length, _word0, _word1, _word2, _word3);
}

/// <summary>Assigns stable, monotonic generations to logical automation documents.</summary>
internal sealed class MarkdownAutomationDocumentGeneration
{
    private MarkdownAutomationDocumentIdentity? _identity;
    private long _generation;

    internal long CurrentGeneration => _generation;

    internal long Publish(MarkdownAutomationDocumentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_identity is not null && _identity.IsEquivalentTo(identity))
            return _generation;

        _identity = identity;
        return ++_generation;
    }

    internal bool IsCurrent(MarkdownAutomationDocumentIdentity identity) =>
        _identity is not null && _identity.IsEquivalentTo(identity);

    /// <summary>
    /// Ends the identity epoch when its snapshot is unpublished. The next
    /// publication receives a new generation even if it has identical inputs.
    /// </summary>
    internal void Invalidate()
    {
        _identity = null;
        _generation++;
    }

    internal static void EnsureCurrent(long expected, long current)
    {
        if (expected != current)
        {
            throw new ElementNotAvailableException(
                "The markdown text range belongs to a document that is no longer available.");
        }
    }
}
