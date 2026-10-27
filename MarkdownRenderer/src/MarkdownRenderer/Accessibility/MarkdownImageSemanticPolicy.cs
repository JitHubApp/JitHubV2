namespace MarkdownRenderer.Accessibility;

internal enum MarkdownImageAccessibilityState
{
    Loading,
    Loaded,
    Error,
}

/// <summary>Pure policy for projecting image alt/link metadata into UIA text.</summary>
internal static class MarkdownImageSemanticPolicy
{
    public static string? GetInlineAccessibleName(
        string? altText,
        bool isLinked,
        string? linkTitle,
        string localizedImageName)
    {
        if (!string.IsNullOrEmpty(altText))
            return altText;
        if (!isLinked)
            return null;
        if (!string.IsNullOrWhiteSpace(linkTitle))
            return linkTitle;
        return localizedImageName;
    }

    public static bool IsDecorativeBlock(string? altText)
        => string.IsNullOrEmpty(altText);
}
