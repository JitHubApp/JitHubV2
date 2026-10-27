using System.Text;

namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Adapts a small set of GitHub/MathJax spellings to equivalent constructs
/// understood by the native CSharpMath parser. The original TeX remains the
/// public semantic, accessibility, copy, and fallback source.
/// </summary>
internal static class MathCompatibilityPreprocessor
{
    private const string BeginAlign = @"\begin{align}";
    private const string BeginAlignStar = @"\begin{align*}";
    private const string EndAlign = @"\end{align}";
    private const string EndAlignStar = @"\end{align*}";
    private const string BeginAligned = @"\begin{aligned}";
    private const string EndAligned = @"\end{aligned}";

    internal static string Normalize(
        string source,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);

        if (!source.Contains(@"\begin{align", StringComparison.Ordinal) &&
            !source.Contains(@"\end{align", StringComparison.Ordinal) &&
            !source.Contains(@"\tag", StringComparison.Ordinal))
        {
            return source;
        }

        var result = new StringBuilder(System.Math.Min(source.Length, maximumLength));
        for (int index = 0; index < source.Length;)
        {
            if ((index & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (IsUnescapedCommentStart(source, index))
            {
                int lineEnd = source.IndexOfAny(['\r', '\n'], index + 1);
                if (lineEnd < 0)
                    lineEnd = source.Length;
                Append(result, source.AsSpan(index, lineEnd - index), maximumLength);
                index = lineEnd;
                continue;
            }

            if (TryReplaceEnvironment(source, ref index, result, maximumLength) ||
                TryReplaceTag(source, ref index, result, maximumLength))
            {
                continue;
            }

            Append(result, source.AsSpan(index, 1), maximumLength);
            index++;
        }

        return result.ToString();
    }

    private static bool TryReplaceEnvironment(
        string source,
        ref int index,
        StringBuilder result,
        int maximumLength)
    {
        if (TryReplace(source, ref index, result, BeginAlignStar, BeginAligned, maximumLength) ||
            TryReplace(source, ref index, result, BeginAlign, BeginAligned, maximumLength) ||
            TryReplace(source, ref index, result, EndAlignStar, EndAligned, maximumLength) ||
            TryReplace(source, ref index, result, EndAlign, EndAligned, maximumLength))
        {
            return true;
        }

        return false;
    }

    private static bool TryReplace(
        string source,
        ref int index,
        StringBuilder result,
        string token,
        string replacement,
        int maximumLength)
    {
        if (!source.AsSpan(index).StartsWith(token, StringComparison.Ordinal))
            return false;

        Append(result, replacement.AsSpan(), maximumLength);
        index += token.Length;
        return true;
    }

    private static bool TryReplaceTag(
        string source,
        ref int index,
        StringBuilder result,
        int maximumLength)
    {
        const string command = @"\tag";
        if (!source.AsSpan(index).StartsWith(command, StringComparison.Ordinal))
            return false;

        int cursor = index + command.Length;
        if (cursor < source.Length && IsAsciiLetter(source[cursor]))
            return false;

        bool omitParentheses = cursor < source.Length && source[cursor] == '*';
        if (omitParentheses)
            cursor++;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
        if (cursor >= source.Length)
            return false;

        int contentStart;
        int contentLength;
        if (source[cursor] == '{')
        {
            contentStart = cursor + 1;
            int depth = 1;
            int end = contentStart;
            for (; end < source.Length; end++)
            {
                char value = source[end];
                if ((value != '{' && value != '}') || IsEscaped(source, end))
                    continue;
                depth += value == '{' ? 1 : -1;
                if (depth == 0)
                    break;
            }

            if (depth != 0)
                return false;
            contentLength = end - contentStart;
            cursor = end + 1;
        }
        else
        {
            contentStart = cursor;
            if (source[cursor] == '\\')
            {
                cursor++;
                while (cursor < source.Length && IsAsciiLetter(source[cursor]))
                    cursor++;
                if (cursor == contentStart + 1 && cursor < source.Length)
                    cursor++;
            }
            else
            {
                cursor++;
            }
            contentLength = cursor - contentStart;
        }

        Append(result, @"\quad\mathrm{".AsSpan(), maximumLength);
        if (!omitParentheses)
            Append(result, "(".AsSpan(), maximumLength);
        Append(result, source.AsSpan(contentStart, contentLength), maximumLength);
        if (!omitParentheses)
            Append(result, ")".AsSpan(), maximumLength);
        Append(result, "}".AsSpan(), maximumLength);
        index = cursor;
        return true;
    }

    private static void Append(StringBuilder result, ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.Length > maximumLength - result.Length)
            throw new MathSceneBudgetException(MathSceneBudget.WorkingMemory);
        result.Append(value);
    }

    private static bool IsUnescapedCommentStart(string source, int index) =>
        source[index] == '%' && !IsEscaped(source, index);

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--)
            slashCount++;
        return (slashCount & 1) != 0;
    }

    private static bool IsAsciiLetter(char value) =>
        (value is >= 'a' and <= 'z') || (value is >= 'A' and <= 'Z');
}
