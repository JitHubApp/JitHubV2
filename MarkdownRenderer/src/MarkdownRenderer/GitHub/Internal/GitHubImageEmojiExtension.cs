using Markdig;
using Markdig.Extensions.Emoji;
using Markdig.Renderers;

namespace MarkdownRenderer.GitHub.Internal;

/// <summary>
/// Adds GitHub's image-backed emoji aliases without replacing Markdig's
/// Unicode emoji mapping. The aliases are static GitHub syntax, so resolving
/// them never requires a startup network request or generated reflection data.
/// </summary>
internal sealed class GitHubImageEmojiExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<GitHubImageEmojiParser>())
        {
            pipeline.InlineParsers.InsertBefore<EmojiParser>(new GitHubImageEmojiParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
