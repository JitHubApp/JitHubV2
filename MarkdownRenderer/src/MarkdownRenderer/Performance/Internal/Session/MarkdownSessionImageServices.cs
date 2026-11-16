using MarkdownRenderer.Performance.Internal.Session.ImageSources;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>
/// The session-owned collaborators every document scope borrows: the work
/// lifetime, admissions, source cache and shared fetcher. The session creates
/// one instance and owns each member's lifetime.
/// </summary>
internal sealed class MarkdownSessionImageServices(
    MarkdownPerformanceOptions options,
    MarkdownPerformanceSessionLifetime lifetime,
    MarkdownSessionAdmissions admissions,
    MarkdownImageSourceCache cache,
    MarkdownSharedImageFetcher fetcher)
{
    internal MarkdownPerformanceOptions Options { get; } = options;
    internal MarkdownPerformanceSessionLifetime Lifetime { get; } = lifetime;
    internal MarkdownSessionAdmissions Admissions { get; } = admissions;
    internal MarkdownImageSourceCache Cache { get; } = cache;
    internal MarkdownSharedImageFetcher Fetcher { get; } = fetcher;
}
