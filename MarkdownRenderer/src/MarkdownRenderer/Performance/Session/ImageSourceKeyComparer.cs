using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Performance;

/// <summary>Compares resolver and document identity by reference, context by value, source ordinally.</summary>
internal sealed class ImageSourceKeyComparer : IEqualityComparer<ImageSourceKey>
{
    internal static readonly ImageSourceKeyComparer Instance = new();

    public bool Equals(ImageSourceKey x, ImageSourceKey y) =>
        ReferenceEquals(x.ResolverIdentity, y.ResolverIdentity) &&
        ReferenceEquals(x.DocumentScope, y.DocumentScope) &&
        Equals(x.Context, y.Context) &&
        string.Equals(x.Source, y.Source, StringComparison.Ordinal);

    public int GetHashCode(ImageSourceKey key) => HashCode.Combine(
        RuntimeHelpers.GetHashCode(key.ResolverIdentity),
        key.DocumentScope is null ? 0 : RuntimeHelpers.GetHashCode(key.DocumentScope),
        key.Context,
        StringComparer.Ordinal.GetHashCode(key.Source));
}
