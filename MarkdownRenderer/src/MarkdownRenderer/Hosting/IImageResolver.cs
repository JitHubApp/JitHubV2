using MarkdownRenderer.Images;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Stable image-resolution service. It deliberately inherits the 0.x contract
/// so existing controls and resolvers remain source compatible.
/// </summary>
public interface IImageResolver : IMarkdownImageResolver
{
}
