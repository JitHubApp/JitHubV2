using System.Globalization;

namespace MarkdownRenderer.Hosting;

/// <summary>Supplies localized renderer strings without exposing resource internals.</summary>
public interface IMarkdownStringProvider
{
    /// <summary>
    /// Returns a localized value for a stable renderer key, or null to use the
    /// library fallback. Implementations must be thread-safe.
    /// </summary>
    string? GetString(string key, CultureInfo culture);
}
