using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Identifies one cached or in-flight image source. A null
/// <see cref="DocumentScope"/> is a shared (host cache-keyed) entry; a
/// non-null scope keeps an unkeyed result local to that document.
/// </summary>
internal readonly record struct ImageSourceKey(
    object ResolverIdentity,
    MarkdownImageResolveContext Context,
    string Source,
    MarkdownPerformanceDocumentScope? DocumentScope);
