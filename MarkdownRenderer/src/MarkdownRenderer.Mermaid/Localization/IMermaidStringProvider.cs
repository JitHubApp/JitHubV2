using System.Globalization;

namespace MarkdownRenderer.Mermaid.Localization;

/// <summary>Resolves localized Mermaid diagnostics without exposing package resources.</summary>
public interface IMermaidStringProvider
{
    /// <summary>
    /// Returns the final localized value for a stable key and its immutable
    /// arguments, or <see langword="null"/> to use the built-in English fallback.
    /// Implementations must be thread-safe and return stable values for the
    /// lifetime of the renderer that owns them.
    /// </summary>
    string? GetString(
        string resourceKey,
        CultureInfo culture,
        IReadOnlyList<object?> arguments);
}
