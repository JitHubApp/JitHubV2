namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Hard safety ceilings for text handed to UI Automation. These limits are
/// deliberately independent of host configuration: accessible descriptions
/// must remain bounded even when the formatter is used directly.
/// </summary>
internal static class MathAccessibilityLimits
{
    internal const int MaximumStructuralInputLength = 64 * 1024;
    internal const int MaximumStructuralSpeechLength = 4 * 1024;
    internal const int MaximumHelpTextLength = 4 * 1024;
    internal const int MaximumStructuralRecursionDepth = 128;
}
