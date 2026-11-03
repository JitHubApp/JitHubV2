namespace MarkdownRenderer.Math;

/// <summary>A stable localized diagnostic over an atomic UTF-16 source range.</summary>
internal sealed class MathFormulaDiagnostic
{
    /// <summary>Creates a formula diagnostic.</summary>
    public MathFormulaDiagnostic(string code, string message, MathSourceRange sourceRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        SourceRange = sourceRange;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the localized diagnostic message.</summary>
    public string Message { get; }

    /// <summary>Gets the half-open UTF-16 range covered by the diagnostic.</summary>
    public MathSourceRange SourceRange { get; }
}
