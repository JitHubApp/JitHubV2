using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.RenderResources;

internal readonly struct SharedLayoutImageContext(
    string? BaseUri,
    string? DocumentPath,
    bool AllowThirdPartyRemoteImages,
    MarkdownDocumentSource? DocumentSource) : IEquatable<SharedLayoutImageContext>
{
    internal readonly string? BaseUri = BaseUri;
    internal readonly string? DocumentPath = DocumentPath;
    internal readonly bool AllowThirdPartyRemoteImages = AllowThirdPartyRemoteImages;
    internal readonly MarkdownDocumentSource? DocumentSource = DocumentSource;

    // MarkdownDocumentSource is itself a public value record. Preserve its
    // value equality, not reference identity, in this private composition key.
    public bool Equals(SharedLayoutImageContext other) =>
        string.Equals(BaseUri, other.BaseUri, StringComparison.Ordinal) &&
        string.Equals(DocumentPath, other.DocumentPath, StringComparison.Ordinal) &&
        AllowThirdPartyRemoteImages == other.AllowThirdPartyRemoteImages &&
        Equals(DocumentSource, other.DocumentSource);

    public override bool Equals(object? obj) => obj is SharedLayoutImageContext other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(BaseUri, DocumentPath, AllowThirdPartyRemoteImages, DocumentSource);
}
