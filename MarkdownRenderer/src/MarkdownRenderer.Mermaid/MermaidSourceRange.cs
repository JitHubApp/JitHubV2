using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// A half-open range in the Mermaid diagram source, measured in UTF-16 code
/// units. Markdown extension integration maps this range into the containing
/// Markdown document before publishing a <c>MarkdownDiagnostic</c>.
/// </summary>
internal readonly record struct MermaidSourceRange(int Start, int Length)
{
    public int End => checked(Start + Length);

    public bool IsValid => Start >= 0 && Length >= 0 && Start <= int.MaxValue - Length;
}
