using System;
using System.Globalization;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Immutable culture/provider pair associated with one committed layout. UIA
/// and visible chrome must resolve both values from the same snapshot while a
/// replacement document is still being built asynchronously.
/// </summary>
internal sealed class MarkdownLocalizationSnapshot
{
    private MarkdownLocalizationSnapshot(
        CultureInfo culture,
        IMarkdownStringProvider? stringProvider)
    {
        Culture = culture;
        StringProvider = stringProvider;
    }

    public CultureInfo Culture { get; }

    public IMarkdownStringProvider? StringProvider { get; }

    public static MarkdownLocalizationSnapshot Capture(
        string? language,
        string? environmentLanguage,
        IMarkdownStringProvider? stringProvider,
        CultureInfo ambientCulture)
    {
        ArgumentNullException.ThrowIfNull(ambientCulture);
        string? effectiveLanguage = string.IsNullOrWhiteSpace(language)
            ? environmentLanguage
            : language;

        CultureInfo culture;
        try
        {
            culture = string.IsNullOrWhiteSpace(effectiveLanguage)
                ? CultureInfo.GetCultureInfo(ambientCulture.Name)
                : CultureInfo.GetCultureInfo(effectiveLanguage);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo(ambientCulture.Name);
        }

        return new MarkdownLocalizationSnapshot(culture, stringProvider);
    }

    public static MarkdownLocalizationSnapshot? SelectCommitted(
        bool hasCommittedDocument,
        MarkdownLocalizationSnapshot? committed) =>
        hasCommittedDocument ? committed : null;
}
