using System;

namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

/// <summary>
/// Maps a half-open selection boundary to the character side owned by that
/// selected range. DirectWrite then resolves the visual caret edge, including
/// run-level bidi reordering.
/// </summary>
internal static class TouchSelectionCaretPolicy
{
    public static TouchSelectionCaretQuery Resolve(
        int textLength,
        int offset,
        bool rangeStart)
    {
        if (textLength <= 0)
            return default;

        offset = Math.Clamp(offset, 0, textLength);
        if (rangeStart)
        {
            return offset < textLength
                ? new TouchSelectionCaretQuery(offset, TrailingSideOfCharacter: false)
                : new TouchSelectionCaretQuery(textLength - 1, TrailingSideOfCharacter: true);
        }

        return offset > 0
            ? new TouchSelectionCaretQuery(offset - 1, TrailingSideOfCharacter: true)
            : new TouchSelectionCaretQuery(0, TrailingSideOfCharacter: false);
    }

    public static double ResolveVisualX(
        double caretX,
        double layoutWidth,
        bool paragraphRightToLeft)
    {
        if (!double.IsFinite(caretX) || !double.IsFinite(layoutWidth) || layoutWidth < 0)
            return double.NaN;

        return paragraphRightToLeft
            ? layoutWidth - caretX
            : caretX;
    }
}
