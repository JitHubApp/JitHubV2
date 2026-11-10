using MarkdownRenderer.Math.Internal.Processing;
using MarkdownRenderer.Math.Internal.Scanning;
using MarkdownRenderer.Math.Localization;
using MarkdownRenderer.Math.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Math.Tests.Internal.Scanning;

/// <summary>Delimiter scanning: exact UTF-16 formula ranges, literal text and delimiter diagnostics.</summary>
public sealed class MathDelimiterScannerTests
{
    [Fact]
    public void Scanner_ReturnsExactInlineUtf16Ranges()
    {
        const string source = "A \U0001F9EA $x+1$ tail";

        MathDelimiterScanResult result = MathDelimiterScanner.Scan(source);

        MathFormulaRequest formula = Assert.Single(result.Formulas);
        Assert.Equal(MathFormulaDisplayMode.Inline, formula.DisplayMode);
        Assert.Equal("$x+1$", formula.OriginalSource);
        Assert.Equal("x+1", formula.TexSource);
        Assert.Equal(new MathSourceRange(5, 5), formula.SourceRange);
        Assert.Equal(new MathSourceRange(6, 3), formula.ContentRange);
        Assert.Equal(formula.OriginalSource, formula.SourceRange.Slice(source).ToString());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Scanner_SupportsMultilineDisplayMathAndSourceOffset()
    {
        const string source = "$$a\n+b$$";

        MathFormulaRequest formula = Assert.Single(MathDelimiterScanner.Scan(source, 20, "ar-SA").Formulas);

        Assert.Equal(MathFormulaDisplayMode.Display, formula.DisplayMode);
        Assert.Equal(new MathSourceRange(20, source.Length), formula.SourceRange);
        Assert.Equal(new MathSourceRange(22, 4), formula.ContentRange);
        Assert.Equal("a\n+b", formula.TexSource);
        Assert.Equal("ar-SA", formula.LanguageTag);
    }

    [Fact]
    public void Scanner_LeavesEscapedAndUnsupportedDelimitersLiteral()
    {
        const string source = @"\$not-math$ \(x\) \[y\] then $z$";

        MathDelimiterScanResult result = MathDelimiterScanner.Scan(source);

        MathFormulaRequest formula = Assert.Single(result.Formulas);
        Assert.Equal("$z$", formula.OriginalSource);
    }

    [Fact]
    public void Scanner_DoesNotTreatCurrencyOrWhitespaceAsInlineMath()
    {
        const string source = "Costs $5 and $10; spaces $ x $; formula $x$.";

        MathFormulaRequest formula = Assert.Single(MathDelimiterScanner.Scan(source).Formulas);

        Assert.Equal("$x$", formula.OriginalSource);
    }

    [Fact]
    public void Scanner_AllowsInlineFormulaBeginningWithTexCommand()
    {
        MathFormulaRequest formula = Assert.Single(
            MathDelimiterScanner.Scan(@"before $\frac{1}{2}$ after").Formulas);

        Assert.Equal(@"\frac{1}{2}", formula.TexSource);
    }

    [Fact]
    public void Scanner_ReportsUnmatchedOpeningDelimiterWithoutInventingFormula()
    {
        MathDelimiterScanResult result = MathDelimiterScanner.Scan("before $$x");

        Assert.Empty(result.Formulas);
        MathFormulaDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MATH001", diagnostic.Code);
        Assert.Equal(new MathSourceRange(7, 2), diagnostic.SourceRange);
    }

    [Fact]
    public void Scanner_LocalizesDelimiterDiagnosticsWithDocumentLanguage()
    {
        var strings = new RecordingStringProvider();

        MathFormulaDiagnostic diagnostic = Assert.Single(
            MathDelimiterScanner.Scan("before $x", 0, "fr-FR", strings).Diagnostics);

        Assert.Equal("Délimiteur mathématique non apparié.", diagnostic.Message);
        Assert.Equal(MathStringKeys.UnmatchedInlineDelimiter, strings.LastKey);
        Assert.Equal("fr-FR", strings.LastLanguageTag);
    }
}
