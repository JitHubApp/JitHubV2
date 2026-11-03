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
                cancellationToken.ThrowIfCancellationRequested();
                sanitized = css;
                return false;
            }

            AddCssRemoval(ref removals, importStart, importEnd - importStart);
            importCursor = importEnd;
        }

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
                    cancellationToken.ThrowIfCancellationRequested();
                    sanitized = css;
                    return false;
                }

                AddCssRemoval(ref removals, start, end + 1 - start);
                cursor = end + 1;
            }
        }

        if (!TryFindMotionDeclarations(css, ref removals, ref charactersUntilCancellationCheck, cancellationToken))
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
        sanitized = string.Create(
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
        return true;
    }

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

            int end = separator + 1;
            char quote = '\0';
            int parentheses = 0;
            bool encounteredNestedRule = false;
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
            if (quote != '\0' || parentheses != 0)
                return false;
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
