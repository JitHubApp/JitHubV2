using MarkdownRenderer.Math.Accessibility;
using MarkdownRenderer.Math.Internal.Processing;
using MarkdownRenderer.Math.Internal.Scanning;
using MarkdownRenderer.Math.Localization;
using MarkdownRenderer.Math.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Math.Tests.Internal.Processing;

/// <summary>
/// Host callback admission: a math owner's callbacks share one bounded gate,
/// and another owner's gate is independent of it.
/// </summary>
// These tests assert wall-clock processing deadlines, so they run in the
// same collection as the other deadline tests.
[Collection("Math deadline timing")]
public sealed class MathHostCallbackGateTests
{
    [Fact]
    public async Task Processor_SaturatedHostCallbackAdmissionTimesOutWithoutSpawningAnotherCallback()
    {
        const int admissionCapacity = MathHostCallbackGate.MaximumConcurrentCallbacks;
        // Every processor here shares one gate, as the processors and the
        // engine extension of one math owner do.
        var gate = new MathHostCallbackGate();
        BlockingFirstCallStringProvider[] blockers = Enumerable.Range(0, admissionCapacity)
            .Select(_ => new BlockingFirstCallStringProvider())
            .ToArray();
        var operations = new List<Task<MathFormulaResult>>(admissionCapacity);

        try
        {
            MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);
            foreach (BlockingFirstCallStringProvider blocker in blockers)
            {
                operations.Add(new MathFormulaProcessor(strings: blocker, callbackGate: gate).ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(10))).AsTask());
            }

            Assert.All(blockers, blocker => Assert.True(
                blocker.FirstCallStarted.Wait(TimeSpan.FromSeconds(2)),
                "A callback worker did not acquire its admission slot."));

            var waitingStrings = new CountingStringProvider();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            MathFormulaResult saturated = await new MathFormulaProcessor(strings: waitingStrings, callbackGate: gate)
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
    public async Task Processor_SaturatedGateDoesNotDelayAnotherOwnersCallbacks()
    {
        var saturatedGate = new MathHostCallbackGate();
        BlockingFirstCallStringProvider[] blockers = Enumerable
            .Range(0, MathHostCallbackGate.MaximumConcurrentCallbacks)
            .Select(_ => new BlockingFirstCallStringProvider())
            .ToArray();
        var operations = new List<Task<MathFormulaResult>>(blockers.Length);
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan("$x$").Formulas);

        try
        {
            foreach (BlockingFirstCallStringProvider blocker in blockers)
            {
                operations.Add(new MathFormulaProcessor(strings: blocker, callbackGate: saturatedGate).ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(10))).AsTask());
            }

            Assert.All(blockers, blocker => Assert.True(
                blocker.FirstCallStarted.Wait(TimeSpan.FromSeconds(2)),
                "A callback worker did not acquire its admission slot."));

            // A processor with its own gate is another owner: the saturated
            // gate neither admits nor delays its callbacks.
            var independentStrings = new CountingStringProvider();
            MathFormulaResult independent = await new MathFormulaProcessor(strings: independentStrings)
                .ProcessAsync(
                    request,
                    new MathProcessingOptions(
                        maximumProcessingTime: TimeSpan.FromSeconds(5)))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(independent.IsAccepted, independent.Diagnostic?.Message);
            Assert.True(independentStrings.CallCount > 0);
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
        const int admissionCapacity = MathHostCallbackGate.MaximumConcurrentCallbacks;
        var gate = new MathHostCallbackGate();
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
                operations.Add(new MathFormulaProcessor(formatter, callbackGate: gate).ProcessAsync(
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
            MathFormulaResult saturated = await new MathFormulaProcessor(saturatedFormatter, callbackGate: gate)
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
            MathFormulaResult probe = await new MathFormulaProcessor(probeFormatter, callbackGate: gate)
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
}
