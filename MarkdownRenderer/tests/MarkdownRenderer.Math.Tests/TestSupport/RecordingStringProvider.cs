using MarkdownRenderer.Math.Localization;

namespace MarkdownRenderer.Math.Tests.TestSupport;

/// <summary>A string provider that records each key and language it is asked for.</summary>
internal sealed class RecordingStringProvider : IMathStringProvider
{
    public string? LastKey { get; private set; }

    public string? LastLanguageTag { get; private set; }

    public string? GetString(string resourceKey, string? languageTag)
    {
        LastKey = resourceKey;
        LastLanguageTag = languageTag;
        return resourceKey == MathStringKeys.UnmatchedInlineDelimiter
            ? "Délimiteur mathématique non apparié."
            : null;
    }
}
