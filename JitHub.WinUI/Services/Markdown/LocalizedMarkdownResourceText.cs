using System.Globalization;
using JitHub.Markdown.Localization;
using JitHub.WinUI.Helpers;

namespace JitHub.Services.Markdown;

/// <summary>
/// Resolves Markdown host text through the app's MRT resources. It holds no
/// state; <see cref="LocalizedResourceText"/> owns the resource lookup.
/// </summary>
internal sealed class LocalizedMarkdownResourceText : IMarkdownResourceText
{
    /// <summary>Gets the app's lookup.</summary>
    public static LocalizedMarkdownResourceText Instance { get; } = new();

    private LocalizedMarkdownResourceText()
    {
    }

    public string GetString(string resourceKey, string fallback, CultureInfo? culture) =>
        LocalizedResourceText.GetString(resourceKey, fallback, culture);
}
