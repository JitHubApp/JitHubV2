namespace MarkdownRenderer.Math;

/// <summary>Identifies the outcome of bounded native formula processing.</summary>
public enum MathFormulaResultKind
{
    /// <summary>The formula produced a usable vector scene.</summary>
    Accepted,
    /// <summary>The source is not valid supported TeX.</summary>
    InvalidSource,
    /// <summary>The source or generated scene exceeded a configured limit.</summary>
    Unsupported,
    /// <summary>An unexpected processing failure occurred.</summary>
    Failed,
}

/// <summary>A stable localized diagnostic over an atomic UTF-16 source range.</summary>
public sealed class MathFormulaDiagnostic
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

/// <summary>
/// Parser- and painter-independent outcome for a formula. Non-accepted outcomes always
/// preserve the exact delimited source for a code-styled fallback.
/// </summary>
public sealed class MathFormulaResult
{
    private MathFormulaResult(
        MathFormulaRequest request,
        MathFormulaResultKind kind,
        MathScene? scene,
        MathAccessibilityDescription? accessibility,
        MathFormulaDiagnostic? diagnostic,
        string? fallbackSource)
    {
        Request = request;
        Kind = kind;
        Scene = scene;
        Accessibility = accessibility;
        Diagnostic = diagnostic;
        FallbackSource = fallbackSource;
    }

    /// <summary>Gets the request associated with this result.</summary>
    public MathFormulaRequest Request { get; }

    /// <summary>Gets the outcome kind.</summary>
    public MathFormulaResultKind Kind { get; }

    /// <summary>Gets whether processing produced an accepted vector scene.</summary>
    public bool IsAccepted => Kind == MathFormulaResultKind.Accepted;

    /// <summary>Gets the immutable vector scene for an accepted formula.</summary>
    public MathScene? Scene { get; }

    /// <summary>Gets accessible formula text when formatting succeeded.</summary>
    public MathAccessibilityDescription? Accessibility { get; }

    /// <summary>Gets a diagnostic for a non-accepted outcome.</summary>
    public MathFormulaDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Exact original source for non-accepted outcomes; never normalized or reconstructed.
    /// </summary>
    public string? FallbackSource { get; }

    /// <summary>Creates a successful result containing an immutable vector scene.</summary>
    public static MathFormulaResult Accepted(
        MathFormulaRequest request,
        MathScene scene,
        MathAccessibilityDescription accessibility)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(accessibility);
        return new MathFormulaResult(request, MathFormulaResultKind.Accepted, scene, accessibility, null, null);
    }

    /// <summary>Creates an invalid-source result with exact-source fallback.</summary>
    public static MathFormulaResult InvalidSource(
        MathFormulaRequest request,
        MathFormulaDiagnostic diagnostic,
        MathAccessibilityDescription? accessibility = null) =>
        Fallback(request, MathFormulaResultKind.InvalidSource, diagnostic, accessibility);

    /// <summary>Creates an unsupported-input result with exact-source fallback.</summary>
    public static MathFormulaResult Unsupported(
        MathFormulaRequest request,
        MathFormulaDiagnostic diagnostic,
        MathAccessibilityDescription? accessibility = null) =>
        Fallback(request, MathFormulaResultKind.Unsupported, diagnostic, accessibility);

    /// <summary>Creates a processing-failure result with exact-source fallback.</summary>
    public static MathFormulaResult Failed(
        MathFormulaRequest request,
        MathFormulaDiagnostic diagnostic,
        MathAccessibilityDescription? accessibility = null) =>
        Fallback(request, MathFormulaResultKind.Failed, diagnostic, accessibility);

    private static MathFormulaResult Fallback(
        MathFormulaRequest request,
        MathFormulaResultKind kind,
        MathFormulaDiagnostic diagnostic,
        MathAccessibilityDescription? accessibility)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!request.SourceRange.Contains(diagnostic.SourceRange))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnostic), "The diagnostic range must be inside the formula range.");
        }

        return new MathFormulaResult(
            request,
            kind,
            scene: null,
            accessibility,
            diagnostic,
            request.OriginalSource);
    }
}
