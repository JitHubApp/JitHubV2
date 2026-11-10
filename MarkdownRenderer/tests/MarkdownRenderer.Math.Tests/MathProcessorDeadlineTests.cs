using System.Globalization;
using MarkdownRenderer.Math.Accessibility;
using MarkdownRenderer.Math.Internal.Processing;
using MarkdownRenderer.Math.Internal.Scanning;
using MarkdownRenderer.Math.Localization;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

/// <summary>Processing deadlines, cancellation and blocked host callbacks.</summary>
// These classes assert wall-clock processing deadlines; one collection keeps
// them from running against each other.
[Collection("Math deadline timing")]
public sealed class MathProcessorDeadlineTests
{
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
}
