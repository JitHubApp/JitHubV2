using System;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Lets a GitHub service expose only the image-resolver capabilities its configured
/// image source can actually honor.
/// </summary>
internal interface IGitHubMarkdownImageResolverProvider
{
    IMarkdownImageResolver CreateMarkdownImageResolver();
}
