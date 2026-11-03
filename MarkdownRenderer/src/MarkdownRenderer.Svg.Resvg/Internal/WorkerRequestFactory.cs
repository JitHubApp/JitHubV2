using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Mints worker requests with fresh request IDs for the pool's current font
/// generation and its per-worker parsed-resource cache budget.
/// </summary>
internal sealed class WorkerRequestFactory(
    ResvgMarkdownSvgRendererOptions options,
    long perWorkerParsedResourceCacheBytes)
{
    private readonly ResvgMarkdownSvgRendererOptions _options = options;
    private long _nextRequestId;
    private long _fontGeneration;

    internal long FontGeneration => Volatile.Read(ref _fontGeneration);

    internal long PerWorkerParsedResourceCacheBytes { get; } = perWorkerParsedResourceCacheBytes;

    internal void AdvanceFontGeneration()
    {
        while (true)
        {
            long current = Volatile.Read(ref _fontGeneration);
            if (current >= WorkerFontCatalogGeneration.MaximumGeneration)
                throw new InvalidOperationException("The SVG font generation reached the protocol limit.");
            if (Interlocked.CompareExchange(ref _fontGeneration, current + 1, current) == current)
                return;
        }
    }

    internal ulong NextRequestId() => checked((ulong)Interlocked.Increment(ref _nextRequestId));

    internal WorkerRequest CreateControlRequest(
        WorkerOperation operation,
        WindowsWorkerProcess worker,
        ulong documentId = 0,
        long? fontGeneration = null) =>
        new(
            operation,
            NextRequestId(),
            worker.NonceLow,
            worker.NonceHigh,
            0,
            new byte[32],
            0,
            0,
            0,
            0,
            null,
            string.Empty,
            MarkdownSvgColorScheme.Light,
            null,
            MarkdownSvgPixelFormat.Rgba8Premultiplied,
            fontGeneration ?? FontGeneration,
            PerWorkerParsedResourceCacheBytes,
            documentId,
            _options);

    internal static string NormalizeLocale(string? locale)
    {
        string normalized = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
        if (normalized.Length > 63 ||
            !normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.UnsupportedContent,
                "The SVG locale is invalid.");
        }
        return normalized;
    }
}
