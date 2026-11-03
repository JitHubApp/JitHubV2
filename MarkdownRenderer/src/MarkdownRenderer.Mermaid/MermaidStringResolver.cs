using System.Collections.ObjectModel;
using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Contains the trust boundary around host localization. A faulty provider can
/// never suppress a renderer diagnostic or exact-source fallback.
/// </summary>
internal static class MermaidStringResolver
{
    private const int MaximumLocalizedStringLength = 4096;
    private static readonly IReadOnlyList<object?> EmptyArguments =
        new ReadOnlyCollection<object?>(Array.Empty<object?>());

    internal static string Resolve(
        IMermaidStringProvider? provider,
        string key,
        CultureInfo culture,
        string englishFormat,
        params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(englishFormat);

        IReadOnlyList<object?> immutableArguments = arguments.Length == 0
            ? EmptyArguments
            : new ReadOnlyCollection<object?>((object?[])arguments.Clone());
        if (provider is not null)
        {
            try
            {
                string? candidate = provider.GetString(key, culture, immutableArguments);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.Length <= MaximumLocalizedStringLength)
                {
                    return candidate;
                }
            }
            catch
            {
                // Host localization is optional and must never suppress the
                // deterministic diagnostic or exact-source fallback.
            }
        }

        try
        {
            return string.Format(culture, englishFormat, arguments);
        }
        catch (FormatException)
        {
            return englishFormat;
        }
    }
}
