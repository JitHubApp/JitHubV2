using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Rewrites author style sheets for the static first frame: removes motion
/// declarations and inert block directives, with bounded, cancellable scans.
/// </summary>
internal static class SvgStaticStyleSheet
{
    internal const int CssCancellationCheckInterval = 4096;

    internal static readonly string[] CssBlockDirectives = ["@font-face", "@keyframes", "@-webkit-keyframes"];

    internal static bool TryCreateStaticStyleSheet(
        string css,
        CancellationToken cancellationToken,
        out string sanitized)
    {
        ArgumentNullException.ThrowIfNull(css);
        cancellationToken.ThrowIfCancellationRequested();

        List<(int Start, int Length)>? removals = null;
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        if (!TryFindImportRemovals(css, ref removals, ref charactersUntilCancellationCheck, cancellationToken) ||
            !TryFindBlockDirectiveRemovals(css, ref removals, ref charactersUntilCancellationCheck, cancellationToken) ||
            !TryFindMotionDeclarations(css, ref removals, ref charactersUntilCancellationCheck, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sanitized = css;
            return false;
        }

        if (removals is null || removals.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sanitized = css;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        sanitized = BlankRemovals(css, removals, cancellationToken);
        return true;
    }

    private static bool TryFindImportRemovals(
        string css,
        ref List<(int Start, int Length)>? removals,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        int importCursor = 0;
        while (TryFindCssToken(
            css,
            "@import",
            importCursor,
            ref charactersUntilCancellationCheck,
            cancellationToken,
            out int importStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindCssStatementEnd(
                css,
                importStart + "@import".Length,
                ref charactersUntilCancellationCheck,
                cancellationToken,
                out int importEnd))
            {
                return false;
            }

            AddCssRemoval(ref removals, importStart, importEnd - importStart);
            importCursor = importEnd;
        }

        return true;
    }

    private static bool TryFindBlockDirectiveRemovals(
        string css,
        ref List<(int Start, int Length)>? removals,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        foreach (string directive in CssBlockDirectives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cursor = 0;
            while (TryFindCssToken(
                css,
                directive,
                cursor,
                ref charactersUntilCancellationCheck,
                cancellationToken,
                out int start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int openingBrace = start + directive.Length;
                while (openingBrace < css.Length && css[openingBrace] != '{')
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    openingBrace++;
                }
                if (openingBrace >= css.Length ||
                    !TryFindCssBlockEnd(
                        css,
                        openingBrace,
                        ref charactersUntilCancellationCheck,
                        cancellationToken,
                        out int end))
                {
                    return false;
                }

                AddCssRemoval(ref removals, start, end + 1 - start);
                cursor = end + 1;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies <paramref name="css"/> with every removal range replaced by
    /// spaces, so offsets and line structure are preserved.
    /// </summary>
    /// <param name="css">The original style sheet.</param>
    /// <param name="removals">The ranges to blank.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The sanitized style sheet.</returns>
    private static string BlankRemovals(
        string css,
        List<(int Start, int Length)> removals,
        CancellationToken cancellationToken) =>
        string.Create(
            css.Length,
            (Css: css, Removals: removals, CancellationToken: cancellationToken),
            static (destination, state) =>
            {
                int charactersUntilCheck = CssCancellationCheckInterval;
                for (int index = 0; index < state.Css.Length; index++)
                {
                    CheckCssCancellation(ref charactersUntilCheck, state.CancellationToken);
                    destination[index] = state.Css[index];
                }

                foreach ((int start, int length) in state.Removals)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    int end = checked(start + length);
                    for (int index = start; index < end; index++)
                    {
                        CheckCssCancellation(ref charactersUntilCheck, state.CancellationToken);
                        destination[index] = ' ';
                    }
                }

                state.CancellationToken.ThrowIfCancellationRequested();
            });

    internal static void AddCssRemoval(
        ref List<(int Start, int Length)>? removals,
        int start,
        int length)
    {
        (removals ??= new List<(int Start, int Length)>()).Add((start, length));
    }

    internal static bool TryFindCssStatementEnd(
        string css,
        int start,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int end)
    {
        char quote = '\0';
        bool comment = false;
        int parentheses = 0;
        for (int cursor = start; cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
                quote = current;
            else if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (current == ';' && parentheses == 0)
            {
                end = cursor + 1;
                return true;
            }
            else if (current == '{' && parentheses == 0)
            {
                end = -1;
                return false;
            }
        }

        end = -1;
        return false;
    }

    internal static bool TryFindMotionDeclarations(
        string css,
        ref List<(int Start, int Length)>? removals,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySkipCssTrivia(css, ref cursor, ref charactersUntilCancellationCheck, cancellationToken))
                continue;

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetter(css[cursor]) || css[cursor] is '-' or '_'))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
            }
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            ReadOnlySpan<char> property = css.AsSpan(nameStart, cursor - nameStart);
            int separator = cursor;
            while (separator < css.Length && char.IsWhiteSpace(css[separator]))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                separator++;
            }
            if (separator >= css.Length || css[separator] != ':')
                continue;

            bool isMotion = IsMotionProperty(property);
            if (!isMotion)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor = separator + 1;
                continue;
            }

            if (!TryFindDeclarationValueEnd(
                css,
                separator + 1,
                ref charactersUntilCancellationCheck,
                cancellationToken,
                out int end,
                out bool encounteredNestedRule))
            {
                return false;
            }
            if (encounteredNestedRule)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor = end + 1;
                continue;
            }

            if (end < css.Length && css[end] == ';')
                end++;
            AddCssRemoval(ref removals, nameStart, end - nameStart);
            cursor = Math.Max(end, separator + 1);
        }

        return true;
    }

    /// <summary>
    /// Scans a motion declaration's value from <paramref name="valueStart"/>
    /// to its terminator, honoring quotes, escapes and parentheses.
    /// </summary>
    /// <param name="css">The style sheet.</param>
    /// <param name="valueStart">The first character after the colon.</param>
    /// <param name="charactersUntilCancellationCheck">The shared cancellation-check countdown.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <param name="end">The index of the terminator, or the end of the sheet.</param>
    /// <param name="encounteredNestedRule">Whether a block opener came before any terminator.</param>
    /// <returns><see langword="false"/> when a quote or parenthesis is left open.</returns>
    private static bool TryFindDeclarationValueEnd(
        string css,
        int valueStart,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int end,
        out bool encounteredNestedRule)
    {
        end = valueStart;
        char quote = '\0';
        int parentheses = 0;
        encounteredNestedRule = false;
        while (end < css.Length)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[end];
            if (quote != '\0')
            {
                if (current == '\\' && end + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    end += 2;
                }
                else
                {
                    if (current == quote)
                        quote = '\0';
                    end++;
                }
                continue;
            }
            if (current is '\'' or '"')
                quote = current;
            else if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (parentheses == 0 && current == '{')
            {
                // A block opener before a declaration terminator means the
                // token was part of a selector or an at-rule prelude (for
                // example `.animation:hover { ... }`), not a property.
                encounteredNestedRule = true;
                break;
            }
            else if (parentheses == 0 && current is (';' or '}'))
                break;
            end++;
        }
        return quote == '\0' && parentheses == 0;
    }

    internal static bool IsMotionProperty(ReadOnlySpan<char> property) =>
        property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
        property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
        property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
        property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase);

    internal static void CheckCssCancellation(
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        if (--charactersUntilCancellationCheck <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            charactersUntilCancellationCheck = CssCancellationCheckInterval;
        }
    }

    internal static bool TrySkipCssTrivia(
        string css,
        ref int cursor,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        if (char.IsWhiteSpace(css[cursor]))
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            cursor++;
            return true;
        }
        if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            cursor += 2;
            while (cursor < css.Length)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (css[cursor] == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor += 2;
                    break;
                }
                cursor++;
            }
            return true;
        }
        if (css[cursor] is '\'' or '"')
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char quote = css[cursor++];
            while (cursor < css.Length && css[cursor] != quote)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (css[cursor] == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor += 2;
                }
                else
                {
                    cursor++;
                }
            }
            if (cursor < css.Length)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
            }
            return true;
        }
        return false;
    }

    internal static bool TryFindCssToken(
        string css,
        string token,
        int start,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int index)
    {
        char quote = '\0';
        bool comment = false;
        for (int cursor = Math.Max(0, start); cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (css.AsSpan(cursor).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                index = cursor;
                return true;
            }
        }

        index = -1;
        return false;
    }

    internal static bool TryFindCssBlockEnd(
        string css,
        int openingBrace,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int end)
    {
        int depth = 0;
        char quote = '\0';
        bool comment = false;
        for (int cursor = openingBrace; cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
            {
                end = cursor;
                return true;
            }
        }

        end = -1;
        return false;
    }
}
