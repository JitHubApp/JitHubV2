using System.Threading.Tasks;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Asynchronously produces declarative content for one exact syntax kind.
/// </summary>
/// <remarks>
/// An engine can invoke a renderer concurrently for independent documents.
/// Implementations and captured services must therefore be thread-safe. The
/// returned operation must observe <see cref="MarkdownExtensionContext.CancellationToken"/>.
/// </remarks>
public delegate ValueTask MarkdownAsyncNodeRenderer(
    MarkdownExtensionContext context,
    MarkdownContentBuilder content);
