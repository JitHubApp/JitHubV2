using Microsoft.UI.Xaml;

namespace JitHub.Markdown.Hosting;

/// <summary>The width constraints a Markdown host applies to its reading region.</summary>
/// <param name="Width">The explicit width, or <see cref="double.NaN"/> to stretch.</param>
/// <param name="MaxWidth">The maximum width, or <see cref="double.PositiveInfinity"/> for none.</param>
/// <param name="HorizontalAlignment">How the region aligns inside the host.</param>
public readonly record struct MarkdownReadingRegionSize(
    double Width,
    double MaxWidth,
    HorizontalAlignment HorizontalAlignment);
