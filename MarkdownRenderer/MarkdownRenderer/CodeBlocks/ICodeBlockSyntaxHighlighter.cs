using System.Threading.Tasks;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Optional provider used to apply syntax highlighting to code blocks.
/// </summary>
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
