using System.Collections.Immutable;
using MarkdownRenderer.Math.Internal;

namespace MarkdownRenderer.Math;

/// <summary>Immutable formulas and diagnostics produced by one delimiter scan.</summary>
public sealed class MathDelimiterScanResult
{
    internal MathDelimiterScanResult(
        ImmutableArray<MathFormulaRequest> formulas,
        ImmutableArray<MathFormulaDiagnostic> diagnostics)
    {
        Formulas = formulas;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets formulas in source order.</summary>
    public ImmutableArray<MathFormulaRequest> Formulas { get; }

    /// <summary>Gets delimiter diagnostics in source order.</summary>
    public ImmutableArray<MathFormulaDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Scans Markdown text-leaf source for unescaped <c>$...$</c> and <c>$$...$$</c>
/// formulas. Backslash and bracket delimiters are intentionally unsupported.
/// </summary>
public static class MathDelimiterScanner
{
    /// <summary>
    /// Scans one Markdown text leaf and returns exact UTF-16 source ranges for
    /// supported inline and display formulas.
    /// </summary>
    public static MathDelimiterScanResult Scan(
        string? source,
        int sourceOffset = 0,
        string? languageTag = null) =>
        Scan(source, sourceOffset, languageTag, strings: null);

    /// <summary>
    /// Scans one Markdown text leaf and localizes delimiter diagnostics through
    /// the supplied string provider.
    /// </summary>
    /// <remarks>
    /// This overload invokes <see cref="IMathStringProvider.GetString"/>
    /// synchronously on the calling thread. Providers must return promptly
    /// without blocking. Callers must run the scan away from the UI thread when
    /// that cannot be guaranteed. Each delimiter diagnostic key is requested at
    /// most once per scan.
    /// </remarks>
    public static MathDelimiterScanResult Scan(
        string? source,
        int sourceOffset,
        string? languageTag,
        IMathStringProvider? strings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        string text = source ?? string.Empty;
        _ = checked(sourceOffset + text.Length);

        var formulas = ImmutableArray.CreateBuilder<MathFormulaRequest>();
        var diagnostics = ImmutableArray.CreateBuilder<MathFormulaDiagnostic>();
        string? unmatchedInlineMessage = null;
        string? unmatchedDisplayMessage = null;
        int position = 0;

        while (position < text.Length)
        {
            int opener = FindNextUnescapedDollar(text, position);
            if (opener < 0)
            {
                break;
            }

            bool isDisplay = opener + 1 < text.Length && text[opener + 1] == '$';
            int delimiterLength = isDisplay ? 2 : 1;
            if (!isDisplay && !CanOpenInline(text, opener))
            {
                position = opener + 1;
                continue;
            }

            int contentStart = opener + delimiterLength;
            int scanEnd = text.Length;
            int closer = isDisplay
                ? FindDisplayCloser(text, contentStart)
                : FindInlineCloser(text, contentStart, out scanEnd);

            if (closer < 0)
            {
                diagnostics.Add(new MathFormulaDiagnostic(
                    "MATH001",
                    ResolveDelimiterDiagnostic(
                        isDisplay,
                        languageTag,
                        strings,
                        ref unmatchedInlineMessage,
                        ref unmatchedDisplayMessage),
                    new MathSourceRange(checked(sourceOffset + opener), delimiterLength)));
                position = isDisplay ? text.Length : scanEnd;
                continue;
            }

            int end = checked(closer + delimiterLength);
            int contentLength = closer - contentStart;
            int absoluteStart = checked(sourceOffset + opener);
            int absoluteContentStart = checked(sourceOffset + contentStart);
            formulas.Add(new MathFormulaRequest(
                text.Substring(opener, end - opener),
                text.Substring(contentStart, contentLength),
                new MathSourceRange(absoluteStart, end - opener),
                new MathSourceRange(absoluteContentStart, contentLength),
                isDisplay ? MathFormulaDisplayMode.Display : MathFormulaDisplayMode.Inline,
                languageTag));

            position = end;
        }

        return new MathDelimiterScanResult(formulas.ToImmutable(), diagnostics.ToImmutable());
    }

    private static string ResolveDelimiterDiagnostic(
        bool isDisplay,
        string? languageTag,
        IMathStringProvider? strings,
        ref string? unmatchedInlineMessage,
        ref string? unmatchedDisplayMessage)
    {
        if (isDisplay)
        {
            return unmatchedDisplayMessage ??= MathStringResolver.Resolve(
                strings,
                MathStringKeys.UnmatchedDisplayDelimiter,
                languageTag);
        }

        return unmatchedInlineMessage ??= MathStringResolver.Resolve(
            strings,
            MathStringKeys.UnmatchedInlineDelimiter,
            languageTag);
    }

    private static int FindNextUnescapedDollar(string text, int start)
    {
        for (int index = start; index < text.Length; index++)
        {
            if (text[index] == '$' && !IsEscaped(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindDisplayCloser(string text, int start)
    {
        for (int index = start; index + 1 < text.Length; index++)
        {
            if (text[index] == '$' && text[index + 1] == '$' && !IsEscaped(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindInlineCloser(string text, int start, out int scanEnd)
    {
        for (int index = start; index < text.Length; index++)
        {
            char value = text[index];
            if (value is '\r' or '\n')
            {
                scanEnd = index + 1;
                return -1;
            }

            if (value != '$' || IsEscaped(text, index))
            {
                continue;
            }

            // A double-dollar token belongs to display math and never closes inline math.
            if (index + 1 < text.Length && text[index + 1] == '$')
            {
                index++;
                continue;
            }

            if (CanCloseInline(text, index))
            {
                scanEnd = index + 1;
                return index;
            }
        }

        scanEnd = text.Length;
        return -1;
    }

    private static bool CanOpenInline(string text, int index)
    {
        int next = index + 1;
        if (next >= text.Length || text[next] == '$' || char.IsWhiteSpace(text[next]))
        {
            return false;
        }

        if (index > 0 &&
            char.IsWhiteSpace(text[index - 1]) &&
            char.IsPunctuation(text[next]) &&
            text[next] != '\\')
        {
            return false;
        }

        // Conservatively leave a currency-like token literal. A host that wants
        // "$2x$" can pass it to the formula processor explicitly.
        return !char.IsDigit(text[next]) || (index > 0 && !char.IsWhiteSpace(text[index - 1]));
    }

    private static bool CanCloseInline(string text, int index)
    {
        if (index == 0 || char.IsWhiteSpace(text[index - 1]))
        {
            return false;
        }

        int next = index + 1;
        return next >= text.Length || !char.IsDigit(text[next]);
    }

    private static bool IsEscaped(string text, int index)
    {
        int slashCount = 0;
        for (int cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
        {
            slashCount++;
        }

        return (slashCount & 1) != 0;
    }
}
