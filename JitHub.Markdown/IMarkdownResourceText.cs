using System.Globalization;

namespace JitHub.Markdown;

/// <summary>
/// Looks up the app's localized text for a resource key. Every caller supplies
/// canonical fallback text, so a missing resource never leaves a blank string.
/// </summary>
public interface IMarkdownResourceText
{
    /// <summary>Returns the localized text for <paramref name="resourceKey"/>.</summary>
    /// <param name="resourceKey">The dotted resource key, such as <c>Markdown.RemoteImage.ProtectedTitle</c>.</param>
    /// <param name="fallback">The canonical text to use when the resource is unavailable.</param>
    /// <param name="culture">The culture to resolve for, or <see langword="null"/> for the app's current language.</param>
    /// <returns>The localized text, or <paramref name="fallback"/>.</returns>
    string GetString(string resourceKey, string fallback, CultureInfo? culture);
}
