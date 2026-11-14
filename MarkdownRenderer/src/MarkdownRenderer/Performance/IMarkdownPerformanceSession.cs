namespace MarkdownRenderer.Performance;

/// <summary>
/// Borrowed, opt-in preparation service. This is a package-boundary contract,
/// not a custom-provider extension point: controls accept the implementation
/// from the optional MarkdownRenderer.Performance package and never own it.
/// </summary>
public interface IMarkdownPerformanceSession
{
    /// <summary>Gets the immutable preparation settings.</summary>
    MarkdownPerformanceOptions Options { get; }

    /// <summary>Returns privacy-safe aggregate counters.</summary>
    MarkdownPerformanceSnapshot GetSnapshot();

    /// <summary>Releases retained source and completed-scene cache data without canceling visible work or retiring active layouts.</summary>
    void Trim();
}
