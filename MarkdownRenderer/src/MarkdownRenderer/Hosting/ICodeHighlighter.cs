using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Asynchronous, cancellation-aware syntax-highlighting service.
/// Implementations and captured state must be thread-safe. A renderer can
/// query <see cref="Revision"/> and invoke
/// <see cref="HighlightAsync(CodeBlockHighlightRequest, CancellationToken)"/>
/// concurrently for different blocks and controls. Implementations must not
/// require a UI synchronization context.
/// </summary>
public interface ICodeHighlighter
{
    /// <summary>
    /// Revision for cache invalidation when the highlighter's grammar or theme changes.
    /// </summary>
    int Revision => 0;

    /// <summary>Highlights one code block without blocking the UI thread.</summary>
    ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken);
}
