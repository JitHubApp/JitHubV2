using Windows.UI;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// A foreground-color span inside a code block's raw displayed code text.
/// </summary>
public readonly record struct CodeBlockHighlightSpan(int Start, int Length, Color Foreground);
