using System.Text.Json;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SourceLookupDiagnosticEvidenceTests
{
    [Fact]
    public void PreviousSchemaTrialWithoutDiagnosticFieldsStillDeserializes()
    {
        const string json = """
            {
              "ordinal": 2,
              "observationCount": 128,
              "operationCount": 524288,
              "samplesElapsedTicks": [4096],
              "samplesNanosecondsPerLookup": [100.0],
              "p95Nanoseconds": 100.0,
              "allocatedBytes": 0,
              "complete": true
            }
            """;

        SourceLookupTrialResult trial = JsonSerializer.Deserialize<SourceLookupTrialResult>(
            json,
            PerformanceReport.JsonOptions)!;

        Assert.Equal(2, trial.Ordinal);
        Assert.Equal(default, trial.StartedUtc);
        Assert.Equal(default, trial.CompletedUtc);
        Assert.Equal(0, trial.ElapsedStopwatchTicks);
        Assert.Equal(-1, trial.ThreadCpuTime100NsTicks);
        Assert.Null(trial.ThreadCycleDelta);
        Assert.Equal(0, trial.Gen0Collections);
        Assert.Equal(0, trial.Gen1Collections);
        Assert.Equal(0, trial.Gen2Collections);
    }

    [Fact]
    public void DiagnosticFieldsRoundTripWithoutChangingMeasuredValues()
    {
        DateTimeOffset started = new(2026, 9, 28, 17, 0, 0, TimeSpan.Zero);
        var trial = new SourceLookupTrialResult
        {
            Ordinal = 1,
            ObservationCount = 128,
            OperationCount = 524_288,
            SamplesElapsedTicks = [4_096],
            SamplesNanosecondsPerLookup = [100],
            P95Nanoseconds = 100,
            AllocatedBytes = 0,
            StartedUtc = started,
            CompletedUtc = started.AddMilliseconds(250),
            ElapsedStopwatchTicks = 2_500_000,
            ThreadCpuTime100NsTicks = 2_000_000,
            ThreadCycleDelta = 900_000,
            Gen0Collections = 0,
            Gen1Collections = 1,
            Gen2Collections = 0,
            Complete = true,
        };

        string json = JsonSerializer.Serialize(trial, PerformanceReport.JsonOptions);
        SourceLookupTrialResult roundTripped = JsonSerializer.Deserialize<SourceLookupTrialResult>(
            json,
            PerformanceReport.JsonOptions)!;

        Assert.Contains("\"startedUtc\"", json, StringComparison.Ordinal);
        Assert.Equal(trial.P95Nanoseconds, roundTripped.P95Nanoseconds);
        Assert.Equal(trial.AllocatedBytes, roundTripped.AllocatedBytes);
        Assert.Equal(trial.StartedUtc, roundTripped.StartedUtc);
        Assert.Equal(trial.CompletedUtc, roundTripped.CompletedUtc);
        Assert.Equal(trial.ElapsedStopwatchTicks, roundTripped.ElapsedStopwatchTicks);
        Assert.Equal(trial.ThreadCpuTime100NsTicks, roundTripped.ThreadCpuTime100NsTicks);
        Assert.Equal(trial.ThreadCycleDelta, roundTripped.ThreadCycleDelta);
        Assert.Equal(trial.Gen1Collections, roundTripped.Gen1Collections);
        Assert.True(roundTripped.Complete);
    }
}
