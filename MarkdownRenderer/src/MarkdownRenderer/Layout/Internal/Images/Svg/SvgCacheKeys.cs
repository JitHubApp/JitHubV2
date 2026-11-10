using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images.Svg;

/// <summary>
/// The shared-cache keys of SVG renders and SVG failures. Both include the
/// renderer identity, provider cache generation, security partition and
/// content hash, so a key never crosses providers, partitions or sources.
/// </summary>
internal static class SvgCacheKeys
{
    /// <summary>The key of a rendered SVG for one locale, color scheme and semantic color.</summary>
    internal static string Content(
        string contentHash,
        string securityPartitionHash,
        long rendererIdentity,
        long providerCacheGeneration,
        string language,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor semanticColor) =>
        $"svg-content:v1:renderer={rendererIdentity}:generation={providerCacheGeneration}:" +
        $"partition={securityPartitionHash}:content={contentHash}:locale={language}:" +
        $"scheme={(int)colorScheme}:color=" +
        semanticColor.Alpha.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Red.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Green.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Blue.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The key of a remembered SVG failure.</summary>
    internal static string Failure(
        string contentHash,
        string securityPartitionHash,
        long rendererIdentity,
        long providerCacheGeneration) =>
        $"svg-failure:v1:renderer={rendererIdentity}:generation={providerCacheGeneration}:" +
        $"{securityPartitionHash}:{contentHash}";
}
