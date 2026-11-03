using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.CodeBlocks;

internal enum CodeBlockHighlightCompletionAction
{
    Drop,
    Publish,
    RetryCurrentSnapshot,
}
