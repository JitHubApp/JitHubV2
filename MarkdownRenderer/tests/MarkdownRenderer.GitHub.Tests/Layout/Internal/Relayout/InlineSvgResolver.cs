using System.Text;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout;

internal sealed class InlineSvgResolver : IMarkdownImageResolver
{
    private readonly byte[] _source = Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
        "<title>Offline test badge</title><desc>Provider metadata is authoritative.</desc>" +
        "<rect width='80' height='40' fill='#d04020'/><text x='2' y='20'>badge</text></svg>");
    private readonly string _cacheKey = "root-local-inline-svg:" + Guid.NewGuid().ToString("N");

    internal string Url { get; } =
        "https://images.example.test/root-local-inline-svg-" + Guid.NewGuid().ToString("N") + ".svg";

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(source, Url, StringComparison.Ordinal))
            throw new InvalidOperationException("The offline SVG resolver received an unexpected source.");
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _source,
            "image/svg+xml",
            new Uri(source),
            _cacheKey)));
    }
}
