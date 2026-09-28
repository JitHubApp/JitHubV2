using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MeasurementVisibilityEvidenceTests
{
    private static readonly DateTimeOffset ReportStarted =
        DateTimeOffset.Parse("2026-09-27T10:00:00Z");
    private static readonly DateTimeOffset ReportCompleted =
        ReportStarted.AddMinutes(2);

    [Fact]
    public void ContinuousForegroundVisibleUnoccludedEvidencePasses()
    {
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);

        Assert.True(MeasurementVisibilityEvidenceValidator.IsValid(
            evidence,
            ReportStarted,
            ReportCompleted,
            out string failure), failure);
    }

    [Fact]
    public void InitialVisibilitySampleUsesTheSameQualificationPredicateAsReleaseEvidence()
    {
        MeasurementVisibilitySample passingSample = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted).Samples[0];

        Assert.True(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(passingSample));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(null));

        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasForeground: false)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasVisible: false)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasMinimized: true)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasCloaked: true)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasWithinWorkArea: false)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, targetWasUnoccluded: false)));
        Assert.False(MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
            Copy(passingSample, captureSucceeded: false)));
    }

    [Fact]
    public async Task ForegroundReadinessWaitsForAQualifiedSample()
    {
        MeasurementVisibilitySample qualified = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted).Samples[0];
        MeasurementVisibilitySample unqualified = Copy(qualified, targetWasForeground: false);
        MeasurementVisibilitySample[] samples = [
            unqualified,
            qualified,
            unqualified,
            qualified,
            qualified,
            qualified,
            qualified,
            qualified,
        ];
        int sampleIndex = 0;

        MeasurementVisibilitySample result =
            await MeasurementVisibilityReadinessWaiter.WaitUntilQualifiedAsync(
                () => samples[Math.Min(sampleIndex++, samples.Length - 1)],
                timeoutMilliseconds: 1_000,
                pollIntervalMilliseconds: 1);

        Assert.Same(qualified, result);
        Assert.Equal(samples.Length, sampleIndex);
    }

    [Fact]
    public async Task ForegroundReadinessFailsClosedWhenTimeoutExpires()
    {
        MeasurementVisibilitySample unqualified = Copy(
            PerformanceVisibilityFixture.CreatePassing(ReportStarted).Samples[0],
            targetWasForeground: false);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => MeasurementVisibilityReadinessWaiter.WaitUntilQualifiedAsync(
                () => unqualified,
                timeoutMilliseconds: 10,
                pollIntervalMilliseconds: 1));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("foreground", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AForegroundLossBetweenPollsInvalidatesTheEntireRun()
    {
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            foregroundLossEvents: 1);

        AssertInvalid(evidence, "foreground");
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("cloaked")]
    [InlineData("offscreen")]
    [InlineData("occluded")]
    [InlineData("capture-failure")]
    public void AnyUnqualifiedWindowSampleInvalidatesTheRun(string mutation)
    {
        MeasurementVisibilityEvidence passing = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);
        MeasurementVisibilitySample[] samples = [.. passing.Samples];
        MeasurementVisibilitySample original = samples[4];
        samples[4] = mutation switch
        {
            "foreground" => Copy(original, targetWasForeground: false),
            "hidden" => Copy(original, targetWasVisible: false),
            "minimized" => Copy(original, targetWasMinimized: true),
            "cloaked" => Copy(original, targetWasCloaked: true),
            "offscreen" => Copy(original, targetWasWithinWorkArea: false),
            "occluded" => Copy(original, targetWasUnoccluded: false),
            "capture-failure" => Copy(original, captureSucceeded: false),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            samples: samples);

        AssertInvalid(evidence, "window");
    }

    [Fact]
    public void MissingDesktopHooksCannotProduceQualifiedEvidence()
    {
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            windowEventHookInstalled: false);

        AssertInvalid(evidence, "monitoring");
    }

    [Fact]
    public void SamplingGapCannotLeaveAnUnobservedPartOfTheRunQualified()
    {
        MeasurementVisibilityEvidence passing = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);
        MeasurementVisibilitySample[] samples = passing.Samples
            .Where(static sample => sample.ElapsedTicks is 0 or 2_000_000 or 20_000_000)
            .ToArray();
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            samples: samples);

        AssertInvalid(evidence, "gap");
    }

    [Fact]
    public void SamplesMustBracketTheCompleteMeasurementInterval()
    {
        MeasurementVisibilityEvidence passing = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            samples: passing.Samples.Take(passing.Samples.Count - 3).ToArray());

        AssertInvalid(evidence, "bracket");
    }

    [Fact]
    public void SamplesMustBeMonotonicAndFitTheMeasurementClock()
    {
        MeasurementVisibilityEvidence passing = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);
        MeasurementVisibilitySample[] samples = [.. passing.Samples];
        samples[3] = Copy(samples[3], elapsedTicks: samples[2].ElapsedTicks);
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            samples: samples);

        AssertInvalid(evidence, "unordered");
    }

    [Fact]
    public void EvidenceCannotPersistWindowTitlesOrOtherUnapprovedDetails()
    {
        MeasurementVisibilityEvidence passing = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted);
        MeasurementVisibilitySample[] samples = [.. passing.Samples];
        samples[1] = Copy(samples[1], source: "Private document title");
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            samples: samples);

        AssertInvalid(evidence, "source");
    }

    [Fact]
    public void VisibilityIntervalMustFitInsideTheSerializedRunInterval()
    {
        MeasurementVisibilityEvidence evidence = PerformanceVisibilityFixture.CreatePassing(
            ReportStarted,
            completedUtc: ReportCompleted.AddSeconds(1));

        AssertInvalid(evidence, "timestamps");
    }

    private static void AssertInvalid(MeasurementVisibilityEvidence evidence, string expectedFailure)
    {
        Assert.False(MeasurementVisibilityEvidenceValidator.IsValid(
            evidence,
            ReportStarted,
            ReportCompleted,
            out string failure));
        Assert.Contains(expectedFailure, failure, StringComparison.OrdinalIgnoreCase);
    }

    private static MeasurementVisibilitySample Copy(
        MeasurementVisibilitySample sample,
        long? elapsedTicks = null,
        bool? captureSucceeded = null,
        bool? targetWasForeground = null,
        bool? targetWasVisible = null,
        bool? targetWasMinimized = null,
        bool? targetWasCloaked = null,
        bool? targetWasWithinWorkArea = null,
        bool? targetWasUnoccluded = null,
        string? source = null)
        => new()
        {
            ElapsedTicks = elapsedTicks ?? sample.ElapsedTicks,
            Source = source ?? sample.Source,
            CaptureSucceeded = captureSucceeded ?? sample.CaptureSucceeded,
            TargetWasForeground = targetWasForeground ?? sample.TargetWasForeground,
            TargetWasVisible = targetWasVisible ?? sample.TargetWasVisible,
            TargetWasMinimized = targetWasMinimized ?? sample.TargetWasMinimized,
            TargetWasCloaked = targetWasCloaked ?? sample.TargetWasCloaked,
            TargetWasWithinWorkArea = targetWasWithinWorkArea ?? sample.TargetWasWithinWorkArea,
            TargetWasUnoccluded = targetWasUnoccluded ?? sample.TargetWasUnoccluded,
        };
}

internal static class PerformanceVisibilityFixture
{
    internal static MeasurementVisibilityEvidence CreatePassing(
        DateTimeOffset reportStartedUtc,
        IReadOnlyList<MeasurementVisibilitySample>? samples = null,
        int foregroundLossEvents = 0,
        int desktopSwitchEvents = 0,
        int captureFailures = 0,
        bool foregroundHookInstalled = true,
        bool desktopSwitchHookInstalled = true,
        bool windowEventHookInstalled = true,
        DateTimeOffset? completedUtc = null,
        TimeSpan? intervalOffset = null,
        TimeSpan? duration = null)
    {
        const long frequency = 10_000_000;
        TimeSpan actualDuration = duration ?? TimeSpan.FromSeconds(2);
        long durationTicks = checked((long)Math.Round(actualDuration.TotalSeconds * frequency));
        DateTimeOffset startedUtc = reportStartedUtc.Add(intervalOffset ?? TimeSpan.FromSeconds(1));
        DateTimeOffset endUtc = completedUtc ?? startedUtc.Add(actualDuration);
        int sampleCount = Math.Max(
            2,
            (int)Math.Ceiling(actualDuration.TotalSeconds / 0.5) + 1);
        List<MeasurementVisibilitySample> actualSamples = samples is null
            ? Enumerable.Range(0, sampleCount)
                .Select(index => new MeasurementVisibilitySample
                {
                    ElapsedTicks = index * durationTicks / (sampleCount - 1),
                    Source = index == 0 ? "start" : index == sampleCount - 1 ? "complete" : "poll",
                    CaptureSucceeded = true,
                    TargetWasForeground = true,
                    TargetWasVisible = true,
                    TargetWasMinimized = false,
                    TargetWasCloaked = false,
                    TargetWasWithinWorkArea = true,
                    TargetWasUnoccluded = true,
                })
                .ToList()
            : [.. samples];

        return new MeasurementVisibilityEvidence
        {
            StartedUtc = startedUtc,
            CompletedUtc = endUtc,
            StopwatchFrequency = frequency,
            DurationTicks = durationTicks,
            ForegroundHookInstalled = foregroundHookInstalled,
            DesktopSwitchHookInstalled = desktopSwitchHookInstalled,
            WindowEventHookInstalled = windowEventHookInstalled,
            ForegroundLossEvents = foregroundLossEvents,
            DesktopSwitchEvents = desktopSwitchEvents,
            CaptureFailures = captureFailures,
            Samples = actualSamples,
        };
    }
}
