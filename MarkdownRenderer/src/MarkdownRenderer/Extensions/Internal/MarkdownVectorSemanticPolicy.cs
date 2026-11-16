using MarkdownRenderer.Extensions.Vectors;

namespace MarkdownRenderer.Extensions.Internal;

/// <summary>
/// Defines the effective behavior of vector semantic flags. Decorative content
/// stays in the drawing scene but is excluded from every interactive and
/// accessible projection; the remaining flags are deliberately independent.
/// </summary>
internal static class MarkdownVectorSemanticPolicy
{
    internal static bool IsExposed(MarkdownVectorSemanticFlags flags) =>
        (flags & MarkdownVectorSemanticFlags.Decorative) == 0;

    internal static bool IsKeyboardFocusable(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Focusable) != 0;

    internal static bool IsSelectable(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Selectable) != 0;

    internal static bool IsLinked(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Linked) != 0;

    internal static bool IsInvokable(
        MarkdownVectorSemanticFlags flags,
        bool hasAction) => IsLinked(flags) && hasAction;
}
