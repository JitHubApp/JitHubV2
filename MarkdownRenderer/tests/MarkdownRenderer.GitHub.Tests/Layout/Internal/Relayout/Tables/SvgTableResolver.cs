using System.Collections.Concurrent;
using System.Text;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

internal sealed class SvgTableResolver : IMarkdownImageResolver
{
    private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly string _cachePrefix = "root-local-table-svg:" + Guid.NewGuid().ToString("N") + ":";

    internal SvgTableResolver()
    {
        string suffix = Guid.NewGuid().ToString("N");
        WideUrl = $"https://images.example.test/table-wide-{suffix}.svg";
        TallUrl = $"https://images.example.test/table-tall-{suffix}.svg";
        AddSvg(WideUrl, "wide", "Wide root title", "Wide accessible description", "#c33b21");
        AddSvg(TallUrl, "tall", "Tall root title", "Tall accessible description", "#2179c3");
    }

    internal string WideUrl { get; }
    internal string TallUrl { get; }
    internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
    internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
        return ValueTask.FromResult(_assets.TryGetValue(source, out MarkdownImageAsset? asset)
            ? MarkdownImageResolution.Resolved(asset)
            : MarkdownImageResolution.Unavailable);
    }

    private void AddSvg(string source, string kind, string title, string description, string fill)
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\">" +
            $"<title>{title}</title><desc>{description}</desc>" +
            $"<rect width=\"100%\" height=\"100%\" fill=\"{fill}\"/>" +
            "<text x=\"0\" y=\"12\">Accessible table vector</text></svg>";
        _assets[source] = new MarkdownImageAsset(
            Encoding.UTF8.GetBytes(svg), "image/svg+xml", new Uri(source),
            _cachePrefix + kind + ":" + Guid.NewGuid().ToString("N"));
    }
}
