using System.Globalization;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

[Collection("Math host callback gate")]
public sealed class MathEngineIntegrationTests
{
    [Fact]
    public async Task UseMathematics_ParsesInlineFormulaIntoReusableVectorContent()
    {
        const string source = "A 🧪 $x+1$ tail";
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        Assert.True(engine.Extensions.HasFeature(MathFeature.ParserFeatureId));
        Assert.True(document.TryGetInlineExtensionContent(
            new SourceSpan(5, 5),
            out MarkdownContentFragment? fragment));
        MarkdownContent formula = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, formula.Kind);
        Assert.Equal(MarkdownAccessibilityRole.Math, formula.AccessibilityRole);
        Assert.Equal(MarkdownStyleRole.Math, formula.StyleRole);
        Assert.Equal(new SourceSpan(5, 5), formula.SourceSpan);
        Assert.Equal("x+1", formula.SemanticText);
        Assert.Contains("Original TeX", formula.AccessibilityDescription, StringComparison.Ordinal);
        Assert.NotNull(formula.VectorScene);
        Assert.NotEmpty(formula.VectorScene!.Commands);
        Assert.Empty(document.Diagnostics);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task UseMathematics_PreservesDisplayContentAndAtomicRangeAcrossLineEndings(string lineEnding)
    {
        string source = string.Join(lineEnding, "before", "$$", @"\frac{1}{2}", "$$", "after");
        int start = source.IndexOf("$$", StringComparison.Ordinal);
        int end = source.LastIndexOf("$$", StringComparison.Ordinal) + 2;
        var formulaRange = new SourceSpan(start, end - start);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        Assert.True(document.TryGetBlockExtensionContent(
            formulaRange,
            out MarkdownContentFragment? fragment));
        MarkdownContent formula = Assert.Single(fragment!.Items);
        Assert.Equal(formulaRange, formula.SourceSpan);
        Assert.Equal(@"\frac{1}{2}", formula.SemanticText);
        Assert.Equal(MarkdownContentKind.VectorScene, formula.Kind);
        Assert.Empty(document.GetCodeBlocks());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task DisplayMath_PreservesMultilineContentAcrossLineEndings(string lineEnding)
    {
        string tex = @"\sum_{i=1}^{n} i" + lineEnding + @"= \frac{n(n+1)}{2}";
        string source = "$$" + lineEnding + tex + lineEnding + "$$";
        var document = await new MarkdownEngineBuilder().UseMathematics().Build().ParseAsync(source);

        Assert.True(document.TryGetBlockExtensionContent(new SourceSpan(0, source.Length), out var fragment));
        var formula = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, formula.Kind);
        Assert.Equal(tex, formula.SemanticText);
        Assert.Empty(document.Diagnostics);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task InvalidDisplayMath_PreservesExactFallbackAndNonemptyDiagnosticRange(string lineEnding)
    {
        const string tex = @"\notacommand{sample}";
        string original = "$$" + lineEnding + tex + lineEnding + "$$";
        string prefix = "before 🧪" + lineEnding + lineEnding;
        string source = prefix + original + lineEnding + "after";
        var document = await new MarkdownEngineBuilder().UseMathematics().Build().ParseAsync(source);

        var range = new SourceSpan(prefix.Length, original.Length);
        Assert.True(document.TryGetBlockExtensionContent(range, out var fragment));
        var fallback = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, fallback.Kind);
        Assert.Equal(original, fallback.Text);
        Assert.Equal(range, fallback.SourceSpan);
        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal("MATH100", diagnostic.Code);
        Assert.Equal(new SourceSpan(prefix.Length + 2 + lineEnding.Length, tex.Length), diagnostic.SourceSpan);
    }

    [Fact]
    public async Task InvalidFormula_EmitsExactCodeStyledFallbackAndDocumentDiagnostic()
    {
        const string source = @"prefix $\notacommand{🧪}$ suffix";
        int start = source.IndexOf('$');
        int end = source.LastIndexOf('$') + 1;
        var formulaRange = new SourceSpan(start, end - start);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        Assert.True(document.TryGetInlineExtensionContent(
            formulaRange,
            out MarkdownContentFragment? fragment));
        MarkdownContent fallback = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.Text, fallback.Kind);
        Assert.Equal(MarkdownStyleRole.InlineCode, fallback.StyleRole);
        Assert.Equal(source.Substring(start, formulaRange.Length), fallback.Text);
        Document.MarkdownDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal("MATH100", diagnostic.Code);
        Assert.Equal(Document.MarkdownDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(formulaRange.Start <= diagnostic.SourceSpan.Start);
        Assert.True(formulaRange.End >= diagnostic.SourceSpan.End);
    }

    [Fact]
    public async Task MathSyntax_RemainsLiteralUntilPackIsExplicitlyEnabled()
    {
        Document.MarkdownDocument document = await new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(@"$x$ and \(y\)");

        Assert.False(document.TryGetInlineExtensionContent(
            new SourceSpan(0, 3),
            out _));
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task UseMathematics_RendersEveryFormulaInRepresentativeParagraphAndDisplayBlock()
    {
        const string source = """
            # Native mathematics

            Inline formulas: $E = mc^2$, and $\frac{1}{2}$ remain atomic.

            $$
            \sum_{i=1}^{n} i = \frac{n(n+1)}{2}
            $$
            """;
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        int firstStart = source.IndexOf("$E", StringComparison.Ordinal);
        int firstEnd = source.IndexOf('$', firstStart + 1) + 1;
        int fractionStart = source.IndexOf("$\\frac", StringComparison.Ordinal);
        int fractionEnd = source.IndexOf('$', fractionStart + 1) + 1;
        int displayStart = source.IndexOf("$$", fractionEnd, StringComparison.Ordinal);
        int displayEnd = source.IndexOf("$$", displayStart + 2, StringComparison.Ordinal) + 2;

        AssertVector(document, new SourceSpan(firstStart, firstEnd - firstStart));
        Assert.True(document.TryGetInlineExtensionContent(
            new SourceSpan(fractionStart, fractionEnd - fractionStart),
            out MarkdownContentFragment? fraction));
        MarkdownContent fractionContent = Assert.Single(fraction!.Items);
        Assert.True(
            fractionContent.Kind == MarkdownContentKind.VectorScene,
            $"Fraction kind {fractionContent.Kind}, text '{fractionContent.Text}', diagnostics: " +
            string.Join(" | ", document.Diagnostics.Select(d => $"{d.Code}:{d.Message}@{d.SourceSpan}")));
        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(displayStart, displayEnd - displayStart),
            out MarkdownContentFragment? display));
        Assert.Equal(MarkdownContentKind.VectorScene, Assert.Single(display!.Items).Kind);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task UseMathematics_ComposesWithGfmAndMarkdownExtraAcrossSoftLineBreaks()
    {
        const string source = """
            # Native mathematics

            Inline formulas stay aligned with surrounding text: $E = mc^2$, and
            the fraction $\frac{1}{2}$ remains one selectable, accessible span.

            $$
            \sum_{i=1}^{n} i = \frac{n(n+1)}{2}
            $$
            """;
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .AddProfile(MarkdownProfiles.MarkdownExtra)
            .UseMathematics()
            .UseExtension(NoOpFenceExtension.Instance)
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        int firstStart = source.IndexOf("$E", StringComparison.Ordinal);
        int firstEnd = source.IndexOf('$', firstStart + 1) + 1;
        int fractionStart = source.IndexOf("$\\frac", StringComparison.Ordinal);
        int fractionEnd = source.IndexOf('$', fractionStart + 1) + 1;
        int displayStart = source.IndexOf("$$", fractionEnd, StringComparison.Ordinal);
        int displayEnd = source.IndexOf("$$", displayStart + 2, StringComparison.Ordinal) + 2;

        AssertVector(document, new SourceSpan(firstStart, firstEnd - firstStart));
        Assert.True(document.TryGetInlineExtensionContent(
            new SourceSpan(fractionStart, fractionEnd - fractionStart),
            out MarkdownContentFragment? fraction));
        MarkdownContent fractionContent = Assert.Single(fraction!.Items);
        Assert.True(
            fractionContent.Kind == MarkdownContentKind.VectorScene,
            $"Fraction kind {fractionContent.Kind}, text '{fractionContent.Text}', diagnostics: " +
            string.Join(" | ", document.Diagnostics.Select(d => $"{d.Code}:{d.Message}@{d.SourceSpan}")));
        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(displayStart, displayEnd - displayStart),
            out MarkdownContentFragment? display));
        Assert.Equal(MarkdownContentKind.VectorScene, Assert.Single(display!.Items).Kind);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task SuperscriptAccessibilitySpeech_IncludesItsOperand()
    {
        const string source = "$E = mc^2$";
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        Assert.True(document.TryGetInlineExtensionContent(
            new SourceSpan(0, source.Length),
            out MarkdownContentFragment? fragment));
        MarkdownContent formula = Assert.Single(fragment!.Items);
        Assert.Contains("2", formula.AccessibilityName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task UseMathematics_FullShowcaseKeepsValidDisplaySceneWhenLaterFormulaIsInvalid(string lineEnding)
    {
        const string template = """
            # Native mathematics

            Inline formulas stay aligned with surrounding text: $E = mc^2$, and
            the fraction $\frac{1}{2}$ remains one selectable, accessible span.

            $$
            \sum_{i=1}^{n} i = \frac{n(n+1)}{2}
            $$

            The formula below is intentionally invalid and must remain visible in
            exact code styling while exposing a diagnostic:

            $\notacommand{sample}$

            Backslash delimiters remain literal in 1.0: \(x + y\) and \[z\].
            """;
        // WinUI's TextBox returns CR-only line endings after assigning the
        // sample source. Exercise the edited text, not just the source literal.
        string source = template.ReplaceLineEndings(lineEnding);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .AddProfile(MarkdownProfiles.MarkdownExtra)
            .UseMathematics()
            .UseExtension(NoOpFenceExtension.Instance)
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(source);

        int displayStart = source.IndexOf("$$", StringComparison.Ordinal);
        int displayEnd = source.IndexOf("$$", displayStart + 2, StringComparison.Ordinal) + 2;
        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(displayStart, displayEnd - displayStart),
            out MarkdownContentFragment? display));
        MarkdownContent displayContent = Assert.Single(display!.Items);
        Assert.True(
            displayContent.Kind == MarkdownContentKind.VectorScene,
            $"Display kind {displayContent.Kind}, text '{displayContent.Text}', diagnostics: " +
            string.Join(" | ", document.Diagnostics.Select(d => $"{d.Code}:{d.Message}@{d.SourceSpan}")));
        Assert.Single(document.Diagnostics);
        Assert.Equal("MATH100", document.Diagnostics[0].Code);
    }

    [Fact]
    public async Task UnclosedDisplayMath_BlockedLocalizationReturnsEnglishAtDeadline()
    {
        const string source = "$$\n";
        using var strings = new BlockingDisplayDiagnosticStringProvider();
        var options = new MathProcessingOptions(
            maximumProcessingTime: TimeSpan.FromMilliseconds(75));
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics(options, strings: strings)
            .WithParseCacheBudgetBytes(0)
            .Build();

        Task<Document.MarkdownDocument> parseTask = engine.ParseAsync(source);
        Document.MarkdownDocument document;
        try
        {
            Assert.True(
                strings.Started.Wait(TimeSpan.FromSeconds(2)),
                "The extension localization callback did not start.");
            document = await parseTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(
                strings.Returned.IsSet,
                "ParseAsync waited for the cancellation-ignoring provider.");
        }
        finally
        {
            strings.Release();
            Assert.True(
                strings.Returned.Wait(TimeSpan.FromSeconds(2)),
                "The detached localization callback did not retire after release.");
        }

        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(0, source.Length),
            out MarkdownContentFragment? fragment));
        MarkdownContent fallback = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, fallback.Kind);
        Assert.Equal(source, fallback.Text);
        Document.MarkdownDiagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal("MATH002", diagnostic.Code);
        Assert.Equal("Unmatched display-math delimiter.", diagnostic.Message);
        Assert.Equal(new SourceSpan(0, 2), diagnostic.SourceSpan);
        Assert.Equal(1, strings.CallCount);
    }

    [Fact]
    public async Task CachedDiagnosticsUseTheEngineConfiguredLocalizationCulture()
    {
        const string source = @"$\notacommand{sample}$";
        var strings = new CultureEchoStringProvider();
        var options = new MathProcessingOptions
        {
            LocalizationCulture = CultureInfo.GetCultureInfo("fr-FR"),
        };
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics(options, strings: strings)
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        Document.MarkdownDocument first = await ParseUnderCultureAsync(
            engine,
            source,
            CultureInfo.GetCultureInfo("en-US"));
        string equalButDistinctSource = new(source.ToCharArray());
        Assert.NotSame(source, equalButDistinctSource);
        Document.MarkdownDocument second = await ParseUnderCultureAsync(
            engine,
            equalButDistinctSource,
            CultureInfo.GetCultureInfo("de-DE"));

        Assert.Same(first, second);
        Assert.Equal("fr-FR", Assert.Single(first.Diagnostics).Message);
        Assert.All(strings.LanguageTags, static tag => Assert.Equal("fr-FR", tag));
    }

    private static async Task<Document.MarkdownDocument> ParseUnderCultureAsync(
        MarkdownEngine engine,
        string source,
        CultureInfo culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            return await engine.ParseAsync(source);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static void AssertVector(
        Document.MarkdownDocument document,
        SourceSpan span)
    {
        Assert.True(document.TryGetInlineExtensionContent(span, out MarkdownContentFragment? fragment));
        Assert.Equal(MarkdownContentKind.VectorScene, Assert.Single(fragment!.Items).Kind);
    }

    private sealed class NoOpFenceExtension : IMarkdownExtension
    {
        internal static NoOpFenceExtension Instance { get; } = new();

        public string Id => "tests.no-op-fence";

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, static (_, _) => { });
    }

    private sealed class CultureEchoStringProvider : IMathStringProvider
    {
        internal List<string?> LanguageTags { get; } = [];

        public string? GetString(string resourceKey, string? languageTag)
        {
            LanguageTags.Add(languageTag);
            return languageTag;
        }
    }

    private sealed class BlockingDisplayDiagnosticStringProvider
        : IMathStringProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _callCount;

        internal ManualResetEventSlim Started { get; } = new(initialState: false);

        internal ManualResetEventSlim Returned { get; } = new(initialState: false);

        internal int CallCount => Volatile.Read(ref _callCount);

        public string? GetString(string resourceKey, string? languageTag)
        {
            if (resourceKey != MathStringKeys.UnmatchedDisplayDelimiter)
                return null;

            Interlocked.Increment(ref _callCount);
            Started.Set();
            try
            {
                _release.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Returned.Set();
            }

            return "Localized unmatched display.";
        }

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Started.Dispose();
            Returned.Dispose();
        }
    }
}
