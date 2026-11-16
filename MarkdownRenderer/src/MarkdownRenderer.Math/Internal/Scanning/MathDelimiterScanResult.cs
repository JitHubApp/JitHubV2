using System.Collections.Immutable;
using MarkdownRenderer.Math.Internal.Processing;

namespace MarkdownRenderer.Math.Internal.Scanning;

/// <summary>Immutable formulas and diagnostics produced by one delimiter scan.</summary>
internal sealed class MathDelimiterScanResult
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
