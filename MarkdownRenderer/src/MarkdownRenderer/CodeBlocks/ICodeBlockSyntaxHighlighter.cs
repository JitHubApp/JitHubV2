using System;
using System.Threading.Tasks;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Compatibility syntax-highlighting provider. Implementations and captured
/// state must be thread-safe: the renderer may read <see cref="Revision"/>
/// and invoke <see cref="HighlightAsync"/> concurrently for different blocks
/// or controls.
/// </summary>
[Obsolete("Use MarkdownRenderer.Hosting.ICodeHighlighter for explicit cancellation support.")]
public interface ICodeBlockSyntaxHighlighter
{
    /// <summary>
    /// Revision for cache invalidation when the highlighter's grammar or theme changes.
    /// </summary>
    int Revision => 0;

    /// <summary>
    /// Returns foreground-color spans for the supplied code, or an empty result when unsupported.
    /// </summary>
    ValueTask<CodeBlockHighlightResult?> HighlightAsync(CodeBlockHighlightRequest request);
}
