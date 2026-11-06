using System;

namespace MarkdownRenderer.Images;

internal readonly struct MarkdownBuiltInImageSource(
    Uri Uri,
    string CacheKey,
    MarkdownBuiltInImageSourceKind Kind)
{
    public readonly Uri Uri = Uri;
    public readonly string CacheKey = CacheKey;
    public readonly MarkdownBuiltInImageSourceKind Kind = Kind;

    public bool CanLoadWithoutResolver =>
        Kind is MarkdownBuiltInImageSourceKind.Data or MarkdownBuiltInImageSourceKind.Local;
}
