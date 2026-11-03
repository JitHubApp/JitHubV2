namespace MarkdownRenderer.Math;

/// <summary>Stable keys requested from <see cref="IMathStringProvider"/>.</summary>
public static class MathStringKeys
{
    /// <summary>Resource key for the atomic formula automation name.</summary>
    public const string AutomationName = "Math.AutomationName";
    /// <summary>Resource key for the original-TeX help-text format.</summary>
    public const string OriginalTex = "Math.OriginalTex";
    /// <summary>Resource key for the fraction structural term.</summary>
    public const string Fraction = "Math.Fraction";
    /// <summary>Resource key for the numerator structural term.</summary>
    public const string Numerator = "Math.Numerator";
    /// <summary>Resource key for the denominator structural term.</summary>
    public const string Denominator = "Math.Denominator";
    /// <summary>Resource key for the end-fraction structural term.</summary>
    public const string EndFraction = "Math.EndFraction";
    /// <summary>Resource key for the square-root structural term.</summary>
    public const string SquareRoot = "Math.SquareRoot";
    /// <summary>Resource key for the end-root structural term.</summary>
    public const string EndRoot = "Math.EndRoot";
    /// <summary>Resource key for the superscript structural term.</summary>
    public const string Superscript = "Math.Superscript";
    /// <summary>Resource key for the end-superscript structural term.</summary>
    public const string EndSuperscript = "Math.EndSuperscript";
    /// <summary>Resource key for the subscript structural term.</summary>
    public const string Subscript = "Math.Subscript";
    /// <summary>Resource key for the end-subscript structural term.</summary>
    public const string EndSubscript = "Math.EndSubscript";
    /// <summary>Resource key for the addition operator term.</summary>
    public const string Plus = "Math.Plus";
    /// <summary>Resource key for the subtraction operator term.</summary>
    public const string Minus = "Math.Minus";
    /// <summary>Resource key for the equality operator term.</summary>
    public const string EqualsOperator = "Math.Equals";
    /// <summary>Resource key for the multiplication operator term.</summary>
    public const string Times = "Math.Times";
    /// <summary>Resource key for the division operator term.</summary>
    public const string DividedBy = "Math.DividedBy";
    /// <summary>Resource key for an invalid-formula diagnostic.</summary>
    public const string InvalidFormula = "Math.InvalidFormula";
    /// <summary>Resource key for an unmatched inline delimiter diagnostic.</summary>
    public const string UnmatchedInlineDelimiter = "Math.UnmatchedInlineDelimiter";
    /// <summary>Resource key for an unmatched display delimiter diagnostic.</summary>
    public const string UnmatchedDisplayDelimiter = "Math.UnmatchedDisplayDelimiter";
    /// <summary>Resource key for a source-length budget diagnostic.</summary>
    public const string SourceTooLong = "Math.SourceTooLong";
    /// <summary>Resource key for a nesting-depth budget diagnostic.</summary>
    public const string NestingTooDeep = "Math.NestingTooDeep";
    /// <summary>Resource key for a scene-complexity budget diagnostic.</summary>
    public const string SceneTooComplex = "Math.SceneTooComplex";
    /// <summary>Resource key for a scene-dimension budget diagnostic.</summary>
    public const string SceneTooLarge = "Math.SceneTooLarge";
    /// <summary>Resource key for a working-memory budget diagnostic.</summary>
    public const string WorkingMemoryExceeded = "Math.WorkingMemoryExceeded";
    /// <summary>Resource key for a processing-deadline diagnostic.</summary>
    public const string ProcessingTimedOut = "Math.ProcessingTimedOut";
    /// <summary>Resource key for an unexpected processing failure.</summary>
    public const string ProcessingFailed = "Math.ProcessingFailed";
}
