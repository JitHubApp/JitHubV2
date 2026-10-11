using System.Text.RegularExpressions;

using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class FirstViewportStageProfileSourceContractTests
{
    [Fact]
    public void StageProfileRemainsQuickOnlyAndUsesTheSelectedCorpusSize()
    {
        string root = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "PerformanceWindow.cs"));
        string selection = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "FirstViewportStageProfileSelection.cs"));
        string profile = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "FirstViewportStageProfile.cs"));

        Assert.Contains("FirstViewportStageProfileSelection.Resolve(", window, StringComparison.Ordinal);
        Assert.Contains("sourceBytes == _stageProfileSourceBytes", window, StringComparison.Ordinal);
        Assert.Contains("scenario.SourceBytes != _stageProfileSourceBytes", window, StringComparison.Ordinal);
        Assert.Contains("SourceUtf16Bytes = _stageProfileSourceBytes", window, StringComparison.Ordinal);
        Assert.Contains("GetCorpusName(_stageProfileSourceBytes)", window, StringComparison.Ordinal);
        Assert.Contains("MARKDOWNRENDERER_PERF_STAGE_PROFILE_SOURCE_BYTES", selection, StringComparison.Ordinal);
        Assert.Contains("if ((outputPathConfigured || sourceSizeConfigured) && !quick)", selection, StringComparison.Ordinal);
        Assert.Contains("quick-non-gating-diagnostic", window, StringComparison.Ordinal);
        Assert.Contains("CurrentSchemaVersion = 2", profile, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(
            window,
            "if\\s*\\(captureStageProfile\\)\\s*_events\\.BeginStageProfilePresentation\\(sequence, sourceBytes\\)",
            RegexOptions.CultureInvariant));
    }

    [Fact]
    public void ThreadAndGcMeasurementsAreCapturedAtTheSynchronousPresentationEvent()
    {
        string root = FindRepositoryRoot();
        string listener = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "PerformanceEventListener.cs"));
        string window = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "PerformanceWindow.cs"));
        string profile = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.PerformanceHarness",
            "FirstViewportStageProfile.cs"));

        Assert.Contains("BeginStageProfilePresentation(sequence, sourceBytes)", window, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(
            listener,
            "if\\s*\\(_stageProfileCapture is not null\\)\\s*CaptureStageProfileMetricsNoLock\\(sequence, generation, sourceUtf16Bytes\\)",
            RegexOptions.CultureInvariant));
        Assert.Contains("ThreadCpuClock.ReadCurrentThreadTicks()", listener, StringComparison.Ordinal);
        Assert.Contains("Environment.CurrentManagedThreadId == capture.ManagedThreadId", listener, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(
            window,
            "if\\s*\\(captureStageProfile\\s*&&\\s*_events\\.TryConsumeStageProfileMetrics\\(presented",
            RegexOptions.CultureInvariant));
        Assert.Contains("value.Sequence != presentation.Sequence", listener, StringComparison.Ordinal);
        Assert.Contains("value.Generation != presentation.Generation", listener, StringComparison.Ordinal);
        Assert.Contains("value.SourceUtf16Bytes != presentation.SourceUtf16Bytes", listener, StringComparison.Ordinal);
        Assert.Contains("GC.CollectionCount(0)", listener, StringComparison.Ordinal);
        Assert.Contains("UiThreadCpuThroughFirstViewportMilliseconds", profile, StringComparison.Ordinal);
        Assert.Contains("ProcessGen0CollectionsThroughFirstViewport", profile, StringComparison.Ordinal);
        Assert.Contains("not a per-publication-stage CPU measurement", window, StringComparison.Ordinal);
        Assert.Contains("not attributed to this presentation", window, StringComparison.Ordinal);

        int eventStart = listener.IndexOf(
            "internal readonly record struct FirstViewportEvent(",
            StringComparison.Ordinal);
        Assert.True(eventStart >= 0);
        int eventTerminator = listener.IndexOf(';', eventStart);
        Assert.True(eventTerminator > eventStart);
        string firstViewportEventDeclaration = listener[eventStart..(eventTerminator + 1)];
        Assert.Equal(
            "internal readonly record struct FirstViewportEvent(long Sequence, long Generation, long SourceUtf16Bytes, double ElapsedMilliseconds);",
            Regex.Replace(firstViewportEventDeclaration, "\\s+", " ")
                .Replace("( ", "(", StringComparison.Ordinal)
                .Trim());

        int disposeStart = listener.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeStart >= 0);
        int nextMethod = listener.IndexOf("private void SignalWaitersNoLock()", disposeStart, StringComparison.Ordinal);
        Assert.True(nextMethod > disposeStart);
        string disposeBody = listener[disposeStart..nextMethod];
        Assert.Contains("_stageProfileCapture = null;", disposeBody, StringComparison.Ordinal);
        Assert.Contains("_stageProfileObservation = null;", disposeBody, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
