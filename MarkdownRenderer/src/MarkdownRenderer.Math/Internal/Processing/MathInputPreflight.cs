
using MarkdownRenderer.Math.Internal.Compilation;
using MarkdownRenderer.Math.Localization;

namespace MarkdownRenderer.Math.Internal.Processing;

/// <summary>
/// Rejects formulas before compilation: empty TeX, TeX over the length or
/// working-memory budget, and unescaped brace nesting over the depth limit.
/// </summary>
internal static class MathInputPreflight
{
    internal static MathFormulaDiagnostic? ValidateInput(
        IMathStringProvider? strings,
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken,
        string languageTag)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.TexSource.Length == 0)
        {
            return MathFormulaDiagnostics.Create(
                strings,
                "MATH100",
                MathStringKeys.InvalidFormula,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        if (request.TexSource.Length > options.MaximumTexLength)
        {
            return MathFormulaDiagnostics.Create(
                strings,
                "MATH101",
                MathStringKeys.SourceTooLong,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        if (MathWorkingMemoryBudget.EstimateSourceBytes(request.TexSource.Length) >
            options.MaximumWorkingMemoryBytes)
        {
            return MathFormulaDiagnostics.Create(
                strings,
                "MATH105",
                MathStringKeys.WorkingMemoryExceeded,
                request.ContentRange,
                languageTag,
                cancellationToken);
        }

        int depth = 0;
        for (int i = 0; i < request.TexSource.Length; i++)
        {
            if ((i & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            char value = request.TexSource[i];
            if ((value != '{' && value != '}') || IsEscaped(request.TexSource, i))
                continue;

            if (value == '{')
            {
                depth++;
                if (depth > options.MaximumNestingDepth)
                {
                    return MathFormulaDiagnostics.Create(
                        strings,
                        "MATH102",
                        MathStringKeys.NestingTooDeep,
                        request.ContentRange,
                        languageTag,
                        cancellationToken);
                }
            }
            else
            {
                depth--;
            }
        }

        return null;
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int cursor = index - 1; cursor >= 0 && source[cursor] == '\\'; cursor--)
            slashCount++;
        return (slashCount & 1) != 0;
    }
}
