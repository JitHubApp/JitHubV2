using System;
using System.Globalization;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;

namespace MarkdownRenderer.Controls.Internal;

/// <summary>
/// Resolves the renderer's localized strings and culture. Owns the
/// localization committed with the published snapshot, so UI Automation and
/// visible chrome agree with the painted document while a replacement with
/// new language inputs is still building.
/// </summary>
internal sealed class RendererLocalization
{
    private readonly IRendererLocalizationHost _host;
    private readonly PublishedLayout _published;
    private MarkdownLocalizationSnapshot? _committed;

    internal RendererLocalization(IRendererLocalizationHost host, PublishedLayout published)
    {
        _host = host;
        _published = published;
    }

    /// <summary>The localization of the published snapshot, if one was committed.</summary>
    internal MarkdownLocalizationSnapshot? Committed => _committed;

    /// <summary>The culture of the effective localization.</summary>
    internal CultureInfo Culture => GetEffective().Culture;

    /// <summary>Records the localization the snapshot being published was built with.</summary>
    internal void Publish(MarkdownLocalizationSnapshot localization) => _committed = localization;

    /// <summary>Captures the language inputs requested now, for the next build.</summary>
    internal MarkdownLocalizationSnapshot CaptureRequested() =>
        MarkdownLocalizationSnapshot.Capture(
            _host.Language,
            _host.EnvironmentLanguage,
            _host.StringProvider,
            CultureInfo.CurrentUICulture);

    /// <summary>
    /// Resolves <paramref name="key"/> through the host string provider,
    /// falling back to the built-in localized string.
    /// </summary>
    internal string Resolve(string key, string fallback) => Resolve(key, fallback, GetEffective());

    /// <summary>
    /// The code block copy button's label: <paramref name="configured"/> when
    /// the host set one, otherwise the localized copy or copied default.
    /// </summary>
    internal string ResolveCodeBlockCopyLabel(string? configured, bool copied) =>
        !string.IsNullOrWhiteSpace(configured) ? configured!
        : copied ? Resolve(MarkdownStringKeys.CodeCopied, MarkdownLocalizedStrings.CodeBlockCopied)
        : Resolve(MarkdownStringKeys.CopyCode, MarkdownLocalizedStrings.CodeBlockCopyAutomationName);

    /// <summary>
    /// Resolves and formats <paramref name="key"/>; a host format that does
    /// not accept <paramref name="arguments"/> falls back to
    /// <paramref name="fallbackFormat"/>.
    /// </summary>
    internal string ResolveFormatted(
        string key,
        string fallbackFormat,
        params object?[] arguments)
    {
        MarkdownLocalizationSnapshot localization = GetEffective();
        string format = Resolve(key, fallbackFormat, localization);
        try
        {
            return string.Format(localization.Culture, format, arguments);
        }
        catch (FormatException)
        {
            return string.Format(
                localization.Culture,
                fallbackFormat,
                arguments);
        }
    }

    private MarkdownLocalizationSnapshot GetEffective()
    {
        MarkdownLocalizationSnapshot? committed =
            MarkdownLocalizationSnapshot.SelectCommitted(
                _published.Snapshot is not null,
                _committed);
        if (committed is not null)
            return committed;

        return CaptureRequested();
    }

    private static string Resolve(
        string key,
        string fallback,
        MarkdownLocalizationSnapshot localization)
    {
        var culture = localization.Culture;
        string localizedFallback = MarkdownLocalizedStrings.Resolve(key, fallback, culture);
        try
        {
            string? value = localization.StringProvider?.GetString(key, culture);
            return string.IsNullOrWhiteSpace(value) ? localizedFallback : value;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] String provider failed for '{key}': {ex.Message}");
            return localizedFallback;
        }
    }
}
