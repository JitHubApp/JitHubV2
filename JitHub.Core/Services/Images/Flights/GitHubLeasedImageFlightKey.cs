using System;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;

namespace JitHub.Core.Services.Images.Flights;

/// <summary>
/// Identifies one leased image flight: a cache key joined only by callers that
/// share the same admission instance, compared by reference.
/// </summary>
internal readonly record struct GitHubLeasedImageFlightKey(
    string CacheKey,
    IMarkdownImageSourceByteAdmission Admission)
{
    public bool Equals(GitHubLeasedImageFlightKey other) =>
        string.Equals(CacheKey, other.CacheKey, StringComparison.Ordinal) &&
        ReferenceEquals(Admission, other.Admission);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(CacheKey),
        RuntimeHelpers.GetHashCode(Admission));
}
