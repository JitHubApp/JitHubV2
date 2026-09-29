using System.Globalization;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

[Collection("Math host callback gate")]
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
    public async Task Processor_EnforcesDeadlineAndPreservesExactSource()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x+1$").Formulas);
        var processor = new MathFormulaProcessor(new DelayingAccessibilityFormatter());
        var timer = System.Diagnostics.Stopwatch.StartNew();

        MathFormulaResult result = await processor.ProcessAsync(
            request,
            new MathProcessingOptions(maximumProcessingTime: TimeSpan.FromMilliseconds(25)));

        timer.Stop();
        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH106", result.Diagnostic!.Code);
        Assert.Equal(request.OriginalSource, result.FallbackSource);
        Assert.NotNull(result.Accessibility);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Deadline took {timer.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task Processor_LocalizationProviderRunsOffCallerThreadAndInvocationReturnsPromptly()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
        using var strings = new BlockingFirstCallStringProvider();
        var processor = new MathFormulaProcessor(strings: strings);
        var invocationReturned = new TaskCompletionSource<Task<MathFormulaResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThreadId = 0;
        var caller = new Thread(() =>
        {
            Volatile.Write(ref callerThreadId, Environment.CurrentManagedThreadId);
            try
            {
                invocationReturned.SetResult(processor.ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(5))).AsTask());
            }
            catch (Exception exception)
            {
                invocationReturned.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Math processor caller-thread boundary test",
        };

        caller.Start();
        Assert.True(
            strings.FirstCallStarted.Wait(TimeSpan.FromSeconds(2)),
            "The localization provider was not invoked.");

        Task<MathFormulaResult> operation;
        try
        {
            operation = await invocationReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.NotEqual(
                Volatile.Read(ref callerThreadId),
                strings.FirstCallThreadId);
            Assert.False(
                strings.FirstCallUsedThreadPool,
                "A blocking host callback consumed a shared thread-pool worker.");
        }
        finally
        {
            strings.ReleaseFirstCall();
            Assert.True(caller.Join(TimeSpan.FromSeconds(2)), "The caller thread did not exit.");
        }

        MathFormulaResult result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsAccepted, result.Diagnostic?.Message);
    }

    [Fact]
    public async Task Processor_BlockedLocalizationProviderReturnsAtDeadlineAndStartsNoLaterCalls()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
        using var strings = new BlockingFirstCallStringProvider();
        var processor = new MathFormulaProcessor(strings: strings);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        Task<MathFormulaResult> operation = processor.ProcessAsync(
            request,
            new MathProcessingOptions(
                maximumProcessingTime: TimeSpan.FromMilliseconds(50))).AsTask();
        Assert.True(
            strings.FirstCallStarted.Wait(TimeSpan.FromSeconds(2)),
            "The localization provider was not invoked.");

        MathFormulaResult result;
        try
        {
            result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            strings.ReleaseFirstCall();
        }

        timer.Stop();
        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH106", result.Diagnostic!.Code);
        Assert.Equal(
            "The formula exceeded the configured processing deadline.",
            result.Diagnostic.Message);
        Assert.Equal("Mathematical expression", result.Accessibility!.AutomationName);
        Assert.Equal("Original TeX: x", result.Accessibility.HelpText);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            $"Deadline took {timer.Elapsed.TotalMilliseconds:F1} ms.");

        Assert.True(
            strings.FirstCallReturned.Wait(TimeSpan.FromSeconds(2)),
            "The blocked provider call did not return after release.");
        Assert.False(
            strings.SecondCallStarted.Wait(TimeSpan.FromMilliseconds(250)),
            "A second provider callback started after the deadline.");
        Assert.Equal(1, strings.CallCount);
    }

    [Fact]
    public async Task Processor_SaturatedHostCallbackAdmissionTimesOutWithoutSpawningAnotherCallback()
    {
        const int admissionCapacity = 4;
        BlockingFirstCallStringProvider[] blockers = Enumerable.Range(0, admissionCapacity)
            .Select(_ => new BlockingFirstCallStringProvider())
            .ToArray();
        var operations = new List<Task<MathFormulaResult>>(admissionCapacity);

        try
        {
            MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
            foreach (BlockingFirstCallStringProvider blocker in blockers)
            {
                operations.Add(new MathFormulaProcessor(strings: blocker).ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(10))).AsTask());
            }

            Assert.All(blockers, blocker => Assert.True(
                blocker.FirstCallStarted.Wait(TimeSpan.FromSeconds(2)),
                "A callback worker did not acquire its admission slot."));

            var waitingStrings = new CountingStringProvider();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            MathFormulaResult saturated = await new MathFormulaProcessor(strings: waitingStrings)
                .ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromMilliseconds(100)))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            timer.Stop();

            Assert.Equal(MathFormulaResultKind.Unsupported, saturated.Kind);
            Assert.Equal("MATH106", saturated.Diagnostic!.Code);
            Assert.Equal(0, waitingStrings.CallCount);
            Assert.True(
                timer.Elapsed < TimeSpan.FromSeconds(1),
                $"Saturated admission took {timer.Elapsed.TotalMilliseconds:F1} ms.");

            foreach (BlockingFirstCallStringProvider blocker in blockers)
                blocker.ReleaseFirstCall();
            MathFormulaResult[] resumed = await Task.WhenAll(operations)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(resumed, result => Assert.True(result.IsAccepted, result.Diagnostic?.Message));
        }
        finally
        {
            foreach (BlockingFirstCallStringProvider blocker in blockers)
                blocker.ReleaseFirstCall();

            try
            {
                await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion while ensuring every blocked
                // callback is released and its admission lease can retire.
            }

            foreach (BlockingFirstCallStringProvider blocker in blockers)
                blocker.Dispose();
        }
    }

    [Fact]
    public async Task Processor_AsyncFormattersRetainAdmissionUntilIgnoredCancellationWorkCompletes()
    {
        const int admissionCapacity = 4;
        var formatters = Enumerable.Range(0, admissionCapacity)
            .Select(_ => new AsyncBlockingAccessibilityFormatter())
            .ToArray();
        var operations = new List<Task<MathFormulaResult>>(admissionCapacity);
        var request = new MathFormulaRequest(
            "$$",
            string.Empty,
            new MathSourceRange(0, 2),
            new MathSourceRange(1, 0),
            MathFormulaDisplayMode.Inline);

        try
        {
            foreach (AsyncBlockingAccessibilityFormatter formatter in formatters)
            {
                operations.Add(new MathFormulaProcessor(formatter).ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromMilliseconds(500))).AsTask());
            }

            Assert.All(formatters, formatter => Assert.True(
                formatter.Started.Wait(TimeSpan.FromSeconds(2)),
                "An async formatter did not acquire its admission slot."));

            MathFormulaResult[] timedOut = await Task.WhenAll(operations)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(timedOut, result =>
            {
                Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
                Assert.Equal("MATH106", result.Diagnostic!.Code);
            });

            var saturatedFormatter = new CountingAccessibilityFormatter();
            MathFormulaResult saturated = await new MathFormulaProcessor(saturatedFormatter)
                .ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromMilliseconds(100)))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MathFormulaResultKind.Unsupported, saturated.Kind);
            Assert.Equal("MATH106", saturated.Diagnostic!.Code);
            Assert.Equal(0, saturatedFormatter.CallCount);

            foreach (AsyncBlockingAccessibilityFormatter formatter in formatters)
                formatter.Release();
            Assert.All(formatters, formatter => Assert.True(
                formatter.Completed.Wait(TimeSpan.FromSeconds(2)),
                "An async formatter did not complete after release."));

            var probeFormatter = new CountingAccessibilityFormatter();
            MathFormulaResult probe = await new MathFormulaProcessor(probeFormatter)
                .ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(2)));
            Assert.Equal(MathFormulaResultKind.InvalidSource, probe.Kind);
            Assert.Equal(1, probeFormatter.CallCount);
        }
        finally
        {
            foreach (AsyncBlockingAccessibilityFormatter formatter in formatters)
                formatter.Release();
            foreach (AsyncBlockingAccessibilityFormatter formatter in formatters)
                formatter.Completed.Wait(TimeSpan.FromSeconds(2));
            foreach (AsyncBlockingAccessibilityFormatter formatter in formatters)
                formatter.Dispose();
        }
    }

    [Fact]
    public async Task Processor_BlockedPostSnapshotProviderCallCannotDefeatDeadlineOrStartAnotherCall()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x+1$").Formulas);
        using var strings = new BlockingFourthCallStringProvider();
        var processor = new MathFormulaProcessor(strings: strings);

        Task<MathFormulaResult> operation = processor.ProcessAsync(
            request,
            new MathProcessingOptions(
                maximumProcessingTime: TimeSpan.FromMilliseconds(250))
            {
                LocalizationCulture = CultureInfo.GetCultureInfo("fr-FR"),
            }).AsTask();
        Assert.True(
            strings.FourthCallStarted.Wait(TimeSpan.FromSeconds(2)),
            "The post-snapshot provider callback was not invoked.");

        MathFormulaResult result;
        try
        {
            result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            strings.ReleaseFourthCall();
        }

        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH106", result.Diagnostic!.Code);
        Assert.Equal("Le traitement a expiré.", result.Diagnostic.Message);
        Assert.Equal("Expression mathématique", result.Accessibility!.AutomationName);
        Assert.Equal("TeX original : x+1", result.Accessibility.HelpText);
        Assert.Equal(4, strings.CallCount);

        Assert.True(
            strings.FourthCallReturned.Wait(TimeSpan.FromSeconds(2)),
            "The blocked provider call did not return after release.");
        Assert.False(
            strings.FifthCallStarted.Wait(TimeSpan.FromMilliseconds(250)),
            "A provider callback started after the post-snapshot call observed cancellation.");
        Assert.Equal(4, strings.CallCount);
    }

    [Fact]
    public async Task Processor_SynchronouslyBlockingReplacementFormatterCannotBlockCallerOrDeadline()
    {
        const string source = "$$";
        var request = new MathFormulaRequest(
            source,
            string.Empty,
            new MathSourceRange(0, source.Length),
            new MathSourceRange(1, 0),
            MathFormulaDisplayMode.Inline);
        using var formatter = new SynchronouslyBlockingAccessibilityFormatter();
        var processor = new MathFormulaProcessor(formatter);
        int callerThreadId = Environment.CurrentManagedThreadId;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        Task<MathFormulaResult> operation = processor.ProcessAsync(
            request,
            new MathProcessingOptions(
                maximumProcessingTime: TimeSpan.FromMilliseconds(75))).AsTask();
        TimeSpan invocationTime = timer.Elapsed;
        Assert.True(
            formatter.Started.Wait(TimeSpan.FromSeconds(2)),
            "The replacement accessibility formatter was not invoked.");

        MathFormulaResult result;
        try
        {
            result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            formatter.Release();
        }

        timer.Stop();
        Assert.NotEqual(callerThreadId, formatter.ThreadId);
        Assert.True(
            invocationTime < TimeSpan.FromMilliseconds(250),
            $"ProcessAsync blocked its caller for {invocationTime.TotalMilliseconds:F1} ms.");
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            $"Deadline took {timer.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH106", result.Diagnostic!.Code);
        Assert.Equal(source, result.FallbackSource);
        Assert.Same(request, result.Request);

        Assert.True(
            formatter.Returned.Wait(TimeSpan.FromSeconds(2)),
            "The formatter did not return after release.");
    }

    [Fact]
    public async Task Processor_TimeoutUsesPrecomputedLocalizedAccessibilityWithoutLateProviderCall()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x+1$").Formulas);
        var formatter = new DelayingAccessibilityFormatter();
        var strings = new TimeoutStringProvider(formatter);
        var processor = new MathFormulaProcessor(formatter, strings);

        MathFormulaResult result = await processor.ProcessAsync(
            request,
            new MathProcessingOptions(maximumProcessingTime: TimeSpan.FromMilliseconds(200))
            {
                LocalizationCulture = CultureInfo.GetCultureInfo("fr-FR"),
            });

        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH106", result.Diagnostic!.Code);
        Assert.Equal("Le traitement a expiré.", result.Diagnostic.Message);
        Assert.Equal("Expression mathématique", result.Accessibility!.AutomationName);
        Assert.Equal("TeX original : x+1", result.Accessibility.HelpText);
        Assert.Equal("fr-FR", strings.LastLanguageTag);
        Assert.Equal(0, strings.CallsAfterFormatterStarted);
    }

    [Fact]
    public async Task Processor_PropagatesCallerCancellation()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new MathFormulaProcessor().ProcessAsync(
                request,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Processor_CancellationWinsWhenBackgroundWorkFaultsConcurrently()
    {
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
        using var formatter = new FaultAfterCancellationAccessibilityFormatter();
        var processor = new MathFormulaProcessor(formatter);
        using var cancellation = new CancellationTokenSource();

        Task<MathFormulaResult> operation = processor.ProcessAsync(
            request,
            cancellationToken: cancellation.Token).AsTask();
        Assert.True(
            formatter.Started.Wait(TimeSpan.FromSeconds(2)),
            "The background formatter did not start.");

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Processor_CancelsPathologicalWorkWithoutWaitingForCompletion()
    {
        string tex = string.Concat(Enumerable.Repeat(@"\frac{x_1^2+y_2^2}{\sqrt{z}}+", 1_500));
        string markdown = "$" + tex + "$";
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan(markdown).Formulas);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new MathFormulaProcessor().ProcessAsync(
                request,
                cancellationToken: cancellation.Token));

        timer.Stop();
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            $"Cancellation took {timer.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task Processor_IsSafeForConcurrentSharedUse()
    {
        MathFormulaRequest request = Assert.Single(
            MathDelimiterScanner.Scan(@"$$\sum_{i=0}^{10}\frac{i}{i+1}$$").Formulas);
        var processor = new MathFormulaProcessor();

        MathFormulaResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => processor.ProcessAsync(request).AsTask()));

        Assert.All(results, result => Assert.True(result.IsAccepted, result.Diagnostic?.Message));
        Assert.Single(results.Select(result => result.Scene!.Width).Distinct());
        Assert.Single(results.Select(result => result.Scene!.Height).Distinct());
        Assert.All(results, result => Assert.NotEmpty(result.Scene!.Commands));
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
        Assert.Equal("The formula is not valid TeX.", result.Diagnostic.Message);
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

    [Fact]
    public void BuiltAssemblyExportsOnlyRendererOwnedTypes()
    {
        Type[] exported = typeof(MathFormulaProcessor).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, type =>
            type.Namespace?.StartsWith("CSharpMath", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exported, type =>
            type.Namespace?.StartsWith("Typography", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(exported.SelectMany(GetPublicSignatureTypes), type =>
            type.Namespace?.StartsWith("CSharpMath", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Typography", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Microsoft.Graphics.Canvas", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BuiltAssemblyContainsOnlyHashLockedMathFonts()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSharpMath.Rendering.Reference_Fonts.AMS-Capital-Blackboard-Bold.otf"] =
                "9578b5b9c86e6ab03846080b9d6fa4f7bc6b3044ac15604b8e7bfd4330295dda",
            ["CSharpMath.Rendering.Reference_Fonts.cyrillic-modern-nmr10.otf"] =
                "5b8e360154685a1117e7f93542b89d5263db58f41a40d6df8f131c5fe5032c0c",
            ["CSharpMath.Rendering.Reference_Fonts.latinmodern-math.otf"] =
                "6075562b771f8b82f0c179e363389684f2dd09de30038269e2628e504bd7be0f",
        };
        System.Reflection.Assembly assembly = typeof(MathFormulaProcessor).Assembly;
        string[] actualNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Keys.OrderBy(name => name, StringComparer.Ordinal), actualNames);
        foreach ((string resourceName, string expectedHash) in expected)
        {
            using Stream stream = Assert.IsAssignableFrom<Stream>(
                assembly.GetManifestResourceStream(resourceName));
            string actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public void BuiltAssemblyOmitsUnsupportedGeneralFontReaders()
    {
        Type[] types = typeof(MathFormulaProcessor).Assembly.GetTypes();

        Assert.DoesNotContain(types, type =>
            type.Namespace?.Contains("WebFont", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("BitmapAndSvgFonts", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("Tables.Variations", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("TrueTypeInterperter", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(types, type => type.Name is
            "SvgTable" or "WoffReader" or "Woff2Reader" or "Glyf" or
            "TrueTypeInterpreter" or "BitmapFontGlyphSource");
    }

    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly;

        foreach (System.Reflection.MethodInfo method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }

        foreach (System.Reflection.PropertyInfo property in type.GetProperties(flags))
            yield return property.PropertyType;
        foreach (System.Reflection.FieldInfo field in type.GetFields(flags))
            yield return field.FieldType;
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

    private sealed class CountingAccessibilityFormatter : IMathAccessibilityFormatter
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(new MathAccessibilityDescription(
                "math",
                request.TexSource,
                request.TexSource,
                request.TexSource));
        }
    }

    private sealed class AsyncBlockingAccessibilityFormatter
        : IMathAccessibilityFormatter, IDisposable
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Started { get; } = new(initialState: false);

        internal ManualResetEventSlim Completed { get; } = new(initialState: false);

        public async ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.Set();
            try
            {
                // Deliberately ignore cancellation to prove admission follows
                // actual host-work lifetime rather than the caller-facing wait.
                await _release.Task.ConfigureAwait(false);
            }
            finally
            {
                Completed.Set();
            }

            return new MathAccessibilityDescription(
                "math",
                request.TexSource,
                request.TexSource,
                request.TexSource);
        }

        internal void Release() => _release.TrySetResult();

        public void Dispose()
        {
            _release.TrySetResult();
            Started.Dispose();
            Completed.Dispose();
        }
    }

    private sealed class DelayingAccessibilityFormatter : IMathAccessibilityFormatter
    {
        private int _started;

        internal bool Started => Volatile.Read(ref _started) != 0;

        public async ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _started, 1);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw new InvalidOperationException("The deadline should cancel first.");
        }
    }

    private sealed class FaultAfterCancellationAccessibilityFormatter
        : IMathAccessibilityFormatter, IDisposable
    {
        internal ManualResetEventSlim Started { get; } = new(initialState: false);

        public async ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Synthetic non-cancellation fault racing cancellation.");
            }

            throw new InvalidOperationException("Unreachable formatter path.");
        }

        public void Dispose() => Started.Dispose();
    }

    private sealed class TimeoutStringProvider(DelayingAccessibilityFormatter formatter)
        : IMathStringProvider
    {
        private int _callsAfterFormatterStarted;

        internal int CallsAfterFormatterStarted => Volatile.Read(ref _callsAfterFormatterStarted);

        internal string? LastLanguageTag { get; private set; }

        public string? GetString(string resourceKey, string? languageTag)
        {
            if (formatter.Started)
                Interlocked.Increment(ref _callsAfterFormatterStarted);

            LastLanguageTag = languageTag;
            return resourceKey switch
            {
                MathStringKeys.AutomationName => "Expression mathématique",
                MathStringKeys.OriginalTex => "TeX original : {0}",
                MathStringKeys.ProcessingTimedOut => "Le traitement a expiré.",
                _ => null,
            };
        }
    }

    private sealed class BlockingFirstCallStringProvider : IMathStringProvider, IDisposable
    {
        private readonly ManualResetEventSlim _releaseFirstCall = new(initialState: false);
        private int _callCount;
        private int _firstCallThreadId;
        private int _firstCallUsedThreadPool;

        internal ManualResetEventSlim FirstCallStarted { get; } = new(initialState: false);

        internal ManualResetEventSlim FirstCallReturned { get; } = new(initialState: false);

        internal ManualResetEventSlim SecondCallStarted { get; } = new(initialState: false);

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int FirstCallThreadId => Volatile.Read(ref _firstCallThreadId);

        internal bool FirstCallUsedThreadPool =>
            Volatile.Read(ref _firstCallUsedThreadPool) != 0;

        public string? GetString(string resourceKey, string? languageTag)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                Volatile.Write(ref _firstCallThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(
                    ref _firstCallUsedThreadPool,
                    Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
                FirstCallStarted.Set();
                try
                {
                    _releaseFirstCall.Wait(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    FirstCallReturned.Set();
                }
            }
            else
            {
                SecondCallStarted.Set();
            }

            return null;
        }

        internal void ReleaseFirstCall() => _releaseFirstCall.Set();

        public void Dispose()
        {
            _releaseFirstCall.Set();
            _releaseFirstCall.Dispose();
            FirstCallStarted.Dispose();
            FirstCallReturned.Dispose();
            SecondCallStarted.Dispose();
        }
    }

    private sealed class CountingStringProvider : IMathStringProvider
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public string? GetString(string resourceKey, string? languageTag)
        {
            Interlocked.Increment(ref _callCount);
            return null;
        }
    }

    private sealed class BlockingFourthCallStringProvider : IMathStringProvider, IDisposable
    {
        private readonly ManualResetEventSlim _releaseFourthCall = new(initialState: false);
        private int _callCount;

        internal ManualResetEventSlim FourthCallStarted { get; } = new(initialState: false);

        internal ManualResetEventSlim FourthCallReturned { get; } = new(initialState: false);

        internal ManualResetEventSlim FifthCallStarted { get; } = new(initialState: false);

        internal int CallCount => Volatile.Read(ref _callCount);

        public string? GetString(string resourceKey, string? languageTag)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 4)
            {
                FourthCallStarted.Set();
                try
                {
                    _releaseFourthCall.Wait(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    FourthCallReturned.Set();
                }
            }
            else if (call >= 5)
            {
                FifthCallStarted.Set();
            }

            return resourceKey switch
            {
                MathStringKeys.AutomationName => "Expression mathématique",
                MathStringKeys.OriginalTex => "TeX original : {0}",
                MathStringKeys.ProcessingTimedOut => "Le traitement a expiré.",
                _ => null,
            };
        }

        internal void ReleaseFourthCall() => _releaseFourthCall.Set();

        public void Dispose()
        {
            _releaseFourthCall.Set();
            _releaseFourthCall.Dispose();
            FourthCallStarted.Dispose();
            FourthCallReturned.Dispose();
            FifthCallStarted.Dispose();
        }
    }

    private sealed class SynchronouslyBlockingAccessibilityFormatter
        : IMathAccessibilityFormatter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _threadId;

        internal ManualResetEventSlim Started { get; } = new(initialState: false);

        internal ManualResetEventSlim Returned { get; } = new(initialState: false);

        internal int ThreadId => Volatile.Read(ref _threadId);

        public ValueTask<MathAccessibilityDescription> FormatAsync(
            MathFormulaRequest request,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            Started.Set();
            try
            {
                _release.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Returned.Set();
            }

            return ValueTask.FromResult(new MathAccessibilityDescription(
                "math",
                request.TexSource,
                request.TexSource,
                request.TexSource));
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

    private sealed class RecordingStringProvider : IMathStringProvider
    {
        public string? LastKey { get; private set; }

        public string? LastLanguageTag { get; private set; }

        public string? GetString(string resourceKey, string? languageTag)
        {
            LastKey = resourceKey;
            LastLanguageTag = languageTag;
            return resourceKey == MathStringKeys.UnmatchedInlineDelimiter
                ? "Délimiteur mathématique non apparié."
                : null;
        }
    }
}
