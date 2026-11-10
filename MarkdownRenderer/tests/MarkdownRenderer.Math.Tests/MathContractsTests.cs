using System.Globalization;
using MarkdownRenderer.Math.Accessibility;
using MarkdownRenderer.Math.Internal.Processing;
using MarkdownRenderer.Math.Internal.Scanning;
using MarkdownRenderer.Math.Internal.Scenes;
using MarkdownRenderer.Math.Localization;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

// These classes assert wall-clock processing deadlines; one collection keeps
// them from running against each other.
[Collection("Math deadline timing")]
public sealed class MathContractsTests
{
    [Theory]
    [InlineData(@"$\notacommand{sample}$")]
    [InlineData(@"$\frac{1}{$")]
    public async Task Processor_InvalidTexReturnsFallbackWithoutFirstChanceExceptions(string source)
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var observing = new AsyncLocal<bool> { Value = true };
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (observing.Value) exceptions.Enqueue(args.Exception);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        MathFormulaResult result;
        try
        {
            var request = Assert.Single(MathDelimiterScanner.Scan(source).Formulas);
            result = await new MathFormulaProcessor().ProcessAsync(request);
        }
        finally
        {
            observing.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Equal("MATH100", result.Diagnostic?.Code);
        Assert.Equal(source, result.FallbackSource);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ProcessorUsesConfiguredCultureWithoutReplacingCallerRequest()
    {
        const string source = @"$\notacommand{sample}$";
        var request = new MathFormulaRequest(
            source,
            source[1..^1],
            new MathSourceRange(0, source.Length),
            new MathSourceRange(1, source.Length - 2),
            MathFormulaDisplayMode.Inline);
        var strings = new RecordingStringProvider();
        var options = new MathProcessingOptions
        {
            LocalizationCulture = CultureInfo.GetCultureInfo("it-IT"),
        };

        MathFormulaResult result = await new MathFormulaProcessor(strings: strings)
            .ProcessAsync(request, options);

        Assert.Same(request, result.Request);
        Assert.Equal("it-IT", strings.LastLanguageTag);
    }

    [Fact]
    public async Task ReplacementFormatterReceivesTheExactCallerRequest()
    {
        const string source = @"$\notacommand{sample}$";
        var request = new MathFormulaRequest(
            source,
            source[1..^1],
            new MathSourceRange(0, source.Length),
            new MathSourceRange(1, source.Length - 2),
            MathFormulaDisplayMode.Inline);
        var formatter = new RecordingAccessibilityFormatter();

        MathFormulaResult result = await new MathFormulaProcessor(formatter)
            .ProcessAsync(
                request,
                new MathProcessingOptions
                {
                    LocalizationCulture = CultureInfo.GetCultureInfo("fr-FR"),
                });

        Assert.Same(request, formatter.Request);
        Assert.Null(formatter.Request!.LanguageTag);
        Assert.Same(request, result.Request);
    }

    [Fact]
    public async Task ExplicitRequestLanguageTagOverridesConfiguredCulture()
    {
        const string source = @"$\notacommand{sample}$";
        var request = new MathFormulaRequest(
            source,
            source[1..^1],
            new MathSourceRange(0, source.Length),
            new MathSourceRange(1, source.Length - 2),
            MathFormulaDisplayMode.Inline,
            "ja-JP");
        var strings = new RecordingStringProvider();

        await new MathFormulaProcessor(strings: strings).ProcessAsync(
            request,
            new MathProcessingOptions
            {
                LocalizationCulture = CultureInfo.GetCultureInfo("it-IT"),
            });

        Assert.Equal("ja-JP", strings.LastLanguageTag);
    }

    [Fact]
    public async Task ExplicitBuiltInFormatterReceivesConfiguredEffectiveCulture()
    {
        const string source = @"$\notacommand{sample}$";
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan(source).Formulas);
        var strings = new RecordingStringProvider();
        var formatter = new DefaultMathAccessibilityFormatter(strings);

        await new MathFormulaProcessor(formatter, strings).ProcessAsync(
            request,
            new MathProcessingOptions
            {
                LocalizationCulture = CultureInfo.GetCultureInfo("fr-FR"),
            });

        Assert.Equal("fr-FR", strings.LastLanguageTag);
    }

    [Fact]
    public void ProcessingOptionsCloneAndFreezeAssignedCulture()
    {
        var supplied = new CultureInfo("fr-FR");
        string expectedSeparator = supplied.NumberFormat.NumberDecimalSeparator;
        var options = new MathProcessingOptions { LocalizationCulture = supplied };

        supplied.NumberFormat.NumberDecimalSeparator = "mutated";

        CultureInfo configured = Assert.IsType<CultureInfo>(options.LocalizationCulture);
        Assert.NotSame(supplied, configured);
        Assert.True(configured.IsReadOnly);
        Assert.Equal(expectedSeparator, configured.NumberFormat.NumberDecimalSeparator);
        Assert.Throws<InvalidOperationException>(() =>
            configured.NumberFormat.NumberDecimalSeparator = "blocked");
    }

    [Fact]
    public async Task MalformedLanguageTagDoesNotRaiseAnInternalCultureException()
    {
        const string malformedTag = "not_a_bcp47_tag_??";
        var request = new MathFormulaRequest(
            "$$",
            string.Empty,
            new MathSourceRange(0, 2),
            new MathSourceRange(1, 0),
            MathFormulaDisplayMode.Inline,
            malformedTag);
        int observedCultureExceptions = 0;
        int observingThread = Environment.CurrentManagedThreadId;
        void OnFirstChance(
            object? _,
            System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == observingThread &&
                args.Exception is CultureNotFoundException)
            {
                observedCultureExceptions++;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        MathFormulaResult result;
        try
        {
            result = await new MathFormulaProcessor().ProcessAsync(request);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Equal(0, observedCultureExceptions);
    }

    [Fact]
    public void InvalidResult_UsesExactOriginalSourceAsFallback()
    {
        const string original = "$\\notacommand{\U0001F9EA}$";
        var request = new MathFormulaRequest(
            original,
            original[1..^1],
            new MathSourceRange(11, original.Length),
            new MathSourceRange(12, original.Length - 2),
            MathFormulaDisplayMode.Inline);
        var diagnostic = new MathFormulaDiagnostic(
            "MATH100",
            "Invalid formula.",
            request.ContentRange);

        MathFormulaResult result = MathFormulaResult.InvalidSource(request, diagnostic);

        Assert.False(result.IsAccepted);
        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Same(request, result.Request);
        Assert.Equal(original, result.FallbackSource);
        Assert.Equal(original.Length, result.FallbackSource!.Length);
    }

    [Fact]
    public void Request_RejectsReconstructedOrOutOfRangeSource()
    {
        Assert.Throws<ArgumentException>(() => new MathFormulaRequest(
            "$x$",
            "x",
            new MathSourceRange(0, 4),
            new MathSourceRange(1, 1),
            MathFormulaDisplayMode.Inline));

        Assert.Throws<ArgumentOutOfRangeException>(() => new MathFormulaRequest(
            "$x$",
            "x",
            new MathSourceRange(10, 3),
            new MathSourceRange(9, 1),
            MathFormulaDisplayMode.Inline));
    }

    [Fact]
    public async Task Processor_CompilesFractionAndRadicalToVectorScene()
    {
        MathFormulaRequest request = Assert.Single(
            MathDelimiterScanner.Scan(@"$$\frac{x^2}{\sqrt{y}}$$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(request);

        Assert.True(result.IsAccepted, result.Diagnostic?.Message);
        Assert.Null(result.FallbackSource);
        MathScene scene = Assert.IsType<MathScene>(result.Scene);
        Assert.True(scene.Width > 0);
        Assert.True(scene.Height > 0);
        Assert.InRange(scene.Baseline, 0, scene.Height);
        Assert.Contains(scene.Commands, command => command.Kind == MathSceneCommandKind.FillPath);
        Assert.Contains(scene.Commands, command => command.Kind == MathSceneCommandKind.StrokeLine);
        Assert.Contains("fraction", result.Accessibility!.StructuralSpeech, StringComparison.Ordinal);
        Assert.Contains("square root", result.Accessibility.StructuralSpeech, StringComparison.Ordinal);
        Assert.Equal(@"\frac{x^2}{\sqrt{y}}", result.Accessibility.CopyText);
    }

    [Fact]
    public async Task Processor_InvalidFormulaPreservesExactAtomicUtf16Source()
    {
        const string markdown = "prefix \U0001F9EA $\\notacommand{\U0001F9EA}$ suffix";
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan(markdown).Formulas);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(request);

        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Null(result.Scene);
        Assert.Equal(request.OriginalSource, result.FallbackSource);
        Assert.Equal(request.OriginalSource, request.SourceRange.Slice(markdown).ToString());
        Assert.Equal(request.TexSource, request.ContentRange.Slice(markdown).ToString());
        Assert.Equal("MATH100", result.Diagnostic!.Code);
    }

    [Fact]
    public async Task Processor_EnforcesSourceDepthAndSceneBudgets()
    {
        var processor = new MathFormulaProcessor();
        MathFormulaRequest sourceRequest = Assert.Single(MathDelimiterScanner.Scan("$abcdef$").Formulas);
        MathFormulaRequest depthRequest = Assert.Single(MathDelimiterScanner.Scan("${{{x}}}}$").Formulas);
        MathFormulaRequest sceneRequest = Assert.Single(MathDelimiterScanner.Scan("$x+y$").Formulas);

        MathFormulaResult source = await processor.ProcessAsync(
            sourceRequest,
            new MathProcessingOptions(maximumTexLength: 2));
        MathFormulaResult depth = await processor.ProcessAsync(
            depthRequest,
            new MathProcessingOptions(maximumNestingDepth: 2));
        MathFormulaResult scene = await processor.ProcessAsync(
            sceneRequest,
            new MathProcessingOptions(maximumSceneCommands: 1));

        Assert.Equal("MATH101", source.Diagnostic!.Code);
        Assert.Equal("MATH102", depth.Diagnostic!.Code);
        Assert.Equal("MATH103", scene.Diagnostic!.Code);
        Assert.Equal(sourceRequest.OriginalSource, source.FallbackSource);
        Assert.Equal(depthRequest.OriginalSource, depth.FallbackSource);
        Assert.Equal(sceneRequest.OriginalSource, scene.FallbackSource);
    }

    [Fact]
    public async Task Processor_EnforcesExplicitWorkingMemoryBudget()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x+y$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(
            request,
            new MathProcessingOptions(maximumWorkingMemoryBytes: 1));

        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH105", result.Diagnostic!.Code);
        Assert.Equal(request.OriginalSource, result.FallbackSource);
        Assert.Null(result.Scene);
    }

    [Fact]
    public async Task Processor_BudgetRejectionKeepsAccessibilityBoundedWithoutCallingFormatter()
    {
        string oversizedTex = new('x', 2_000_000);
        var oversizedRequest = new MathFormulaRequest(
            oversizedTex,
            oversizedTex,
            new MathSourceRange(0, oversizedTex.Length),
            new MathSourceRange(0, oversizedTex.Length),
            MathFormulaDisplayMode.Display);
        var formatter = new CountingAccessibilityFormatter();
        var processor = new MathFormulaProcessor(formatter);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        MathFormulaResult oversized = await processor.ProcessAsync(
            oversizedRequest,
            new MathProcessingOptions(
                maximumTexLength: 128,
                maximumProcessingTime: TimeSpan.FromSeconds(2)));

        timer.Stop();
        Assert.Equal(MathFormulaResultKind.Unsupported, oversized.Kind);
        Assert.Equal("MATH101", oversized.Diagnostic!.Code);
        Assert.Same(oversizedTex, oversized.FallbackSource);
        Assert.Same(oversizedTex, oversized.Accessibility!.CopyText);
        Assert.True(oversized.Accessibility.StructuralSpeech.Length <= 256);
        Assert.True(oversized.Accessibility.HelpText.Length <= 2048);
        Assert.EndsWith("\u2026", oversized.Accessibility.StructuralSpeech, StringComparison.Ordinal);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            $"Oversized rejection took {timer.Elapsed.TotalMilliseconds:F1} ms.");

        MathFormulaRequest memoryRequest = Assert.Single(
            MathDelimiterScanner.Scan("$x+y$").Formulas);
        MathFormulaResult memory = await processor.ProcessAsync(
            memoryRequest,
            new MathProcessingOptions(maximumWorkingMemoryBytes: 1));

        Assert.Equal(MathFormulaResultKind.Unsupported, memory.Kind);
        Assert.Equal("MATH105", memory.Diagnostic!.Code);
        Assert.Equal(0, formatter.CallCount);
    }

    [Fact]
    public async Task AccessibilityFormatterFailureFallsBackWithoutBlankingFormula()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x+1$").Formulas);
        var processor = new MathFormulaProcessor(new ThrowingAccessibilityFormatter());

        MathFormulaResult result = await processor.ProcessAsync(request);

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.Scene);
        Assert.Equal("Mathematical expression", result.Accessibility!.AutomationName);
        Assert.Equal("x plus 1", result.Accessibility.StructuralSpeech);
    }

    [Fact]
    public async Task ThrowingStringProviderCannotSuppressExactFallbackOrDiagnostics()
    {
        var strings = new ThrowingStringProvider();
        MathFormulaDiagnostic delimiterDiagnostic = Assert.Single(
            MathDelimiterScanner.Scan("before $x", 0, "en-US", strings).Diagnostics);
        MathFormulaRequest invalid = Assert.Single(
            MathDelimiterScanner.Scan(@"$\notacommand{U0001F9EA}$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor(strings: strings).ProcessAsync(invalid);

        Assert.Equal("Unmatched inline-math delimiter.", delimiterDiagnostic.Message);
        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Equal("MATH100", result.Diagnostic!.Code);
        Assert.Equal(
            "The formula is invalid or uses TeX syntax this renderer does not support.",
            result.Diagnostic.Message);
        Assert.Equal(invalid.OriginalSource, result.FallbackSource);
        Assert.Equal("Mathematical expression", result.Accessibility!.AutomationName);
        Assert.Equal(invalid.TexSource, result.Accessibility.CopyText);
    }

    [Fact]
    public async Task MalformedLocalizedFormatFallsBackToSafeEnglishText()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor(
            strings: new MalformedStringProvider()).ProcessAsync(request);

        Assert.True(result.IsAccepted, result.Diagnostic?.Message);
        Assert.Equal("Mathematical expression", result.Accessibility!.AutomationName);
        Assert.Equal("Original TeX: x", result.Accessibility.HelpText);
    }

    [Fact]
    public async Task HugeLocalizedAlignmentCannotAmplifyAccessibilityHelpText()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor(
            strings: new HugeAlignmentStringProvider()).ProcessAsync(request);

        Assert.True(result.IsAccepted, result.Diagnostic?.Message);
        Assert.Equal("Original TeX: x", result.Accessibility!.HelpText);
        Assert.True(result.Accessibility.HelpText.Length < 100);
    }

    [Fact]
    public async Task LocalizedOriginalTexTemplateSupportsEscapedLiteralBraces()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);

        MathFormulaResult result = await new MathFormulaProcessor(
            strings: new EscapedBraceStringProvider()).ProcessAsync(request);

        Assert.True(result.IsAccepted, result.Diagnostic?.Message);
        Assert.Equal("TeX {source}: x", result.Accessibility!.HelpText);
    }

    private sealed class ThrowingAccessibilityFormatter : IMathAccessibilityFormatter
    {
        public ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic formatter failure.");
    }

    private sealed class RecordingAccessibilityFormatter : IMathAccessibilityFormatter
    {
        public MathFormulaRequest? Request { get; private set; }

        public ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(new MathAccessibilityDescription(
                "math",
                request.TexSource,
                request.TexSource,
                request.TexSource));
        }
    }

    private sealed class ThrowingStringProvider : IMathStringProvider
    {
        public string? GetString(string resourceKey, string? languageTag) =>
            throw new InvalidOperationException("Synthetic localization failure.");
    }

    private sealed class MalformedStringProvider : IMathStringProvider
    {
        public string? GetString(string resourceKey, string? languageTag) => resourceKey switch
        {
            MathStringKeys.AutomationName => " ",
            MathStringKeys.OriginalTex => "Original TeX: {999}",
            _ => null,
        };
    }

    private sealed class HugeAlignmentStringProvider : IMathStringProvider
    {
        public string? GetString(string resourceKey, string? languageTag) =>
            resourceKey == MathStringKeys.OriginalTex
                ? "{0,1000000}"
                : null;
    }

    private sealed class EscapedBraceStringProvider : IMathStringProvider
    {
        public string? GetString(string resourceKey, string? languageTag) =>
            resourceKey == MathStringKeys.OriginalTex
                ? "TeX {{source}}: {0}"
                : null;
    }
}
