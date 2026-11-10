using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Controls.Images;

internal sealed class PendingImagePrefetch(
    Markdig.Syntax.MarkdownDocument Document,
    SafeHtmlRenderPolicy? SafeHtmlPolicy,
    IMarkdownImagePrefetcher Prefetcher,
    MarkdownImageResolveContext Context,
    CancellationToken CancellationToken)
{
    public readonly Markdig.Syntax.MarkdownDocument Document = Document;
    public readonly SafeHtmlRenderPolicy? SafeHtmlPolicy = SafeHtmlPolicy;
    public readonly IMarkdownImagePrefetcher Prefetcher = Prefetcher;
    public readonly MarkdownImageResolveContext Context = Context;
    public readonly CancellationToken CancellationToken = CancellationToken;
}
