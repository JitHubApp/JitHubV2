namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

/// <summary>
/// Comment-, quote- and escape-aware CSS scanning primitives shared by the
/// static style-sheet rewrite and the inline style rules. Every scan charges
/// the caller's shared countdown and checks cancellation at a fixed interval.
/// </summary>
internal static class SvgCssScanner
{
    internal const int CssCancellationCheckInterval = 4096;

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
