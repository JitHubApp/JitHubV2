namespace MarkdownRenderer.Extensions;

/// <summary>
/// Produces declarative content for one exact syntax kind.
/// </summary>
/// <remarks>
/// An engine can invoke a renderer concurrently for independent documents.
/// Implementations and captured services must therefore be thread-safe.
/// </remarks>
public delegate void MarkdownNodeRenderer(
    MarkdownExtensionContext context,
    MarkdownContentBuilder content);
