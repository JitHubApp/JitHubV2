using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-017 (Phase 3: every README whose Mermaid diagram or display math was
/// scrolled into view kept its source fallback and reported "Loading Embedded
/// content" forever). The visible-scene probe ran while the scrolled-to band
/// was still being measured, found no measured band, and nothing re-ran it
/// once measurement and scene preparation finished without a new layout
/// commit. These source contracts pin the retry; live Sandbox runs of the
/// Mermaid READMEs verify the behavior.
/// </summary>
public sealed class DeferredSceneProbeRetryContractTests
{
    [Fact]
    public void EveryUnverifiedVisibleProbeRequestsARetryAfterPaint()
    {
        string body = MethodBody(ReadSource("Performance", "MarkdownProgressiveScenePlan.cs"), "internal bool ScheduleVisible(object documentOwner)");
        int noViewport = body.IndexOf("_lastVisibleProbe = \"no-viewport\";", StringComparison.Ordinal);
        int busy = body.IndexOf("_lastVisibleProbe = \"busy\";", StringComparison.Ordinal);
        int unverified = body.IndexOf("if (visibleMarkerCount < 0)", StringComparison.Ordinal);
        Assert.True(noViewport >= 0 && busy > noViewport && unverified > busy);

        const string retry = "control.RequestDeferredSceneProbeAfterPaint();";
        Assert.Contains(retry, body[noViewport..busy], StringComparison.Ordinal);
        Assert.Contains(retry, body[busy..unverified], StringComparison.Ordinal);
        int unverifiedEnd = body.IndexOf("return true;", unverified, StringComparison.Ordinal);
        Assert.Contains(retry, body[unverified..unverifiedEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulPaintConsumesTheRetryAndReschedulesVisibleScenes()
    {
        string control = ReadSource("Controls", "MarkdownRendererControl.cs");
        Assert.Contains(
            "internal void RequestDeferredSceneProbeAfterPaint()\n        => Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 1);",
            control.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        int consume = control.IndexOf("Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 0) != 0", StringComparison.Ordinal);
        Assert.True(consume >= 0, "A successful paint must consume the retry exactly once.");
        string guard = control[System.Math.Max(0, consume - 80)..consume];
        Assert.Contains("paintedRegion", guard, StringComparison.Ordinal);
        int schedule = control.IndexOf("DispatcherQueue?.TryEnqueue(ScheduleVisibleDeferredScenes);", consume, StringComparison.Ordinal);
        Assert.True(schedule > consume && schedule - consume < 600, "The consumed retry must reschedule the visible probe.");

        string release = MethodBody(control, "private void ReleaseLoadedResources()");
        Assert.Contains("Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 0);", release, StringComparison.Ordinal);
    }

    [Fact]
    public void LazyCompletionWithoutANewRevisionReprobesVisibleScenes()
    {
        // The live cause: the band finished measuring and preparing its scene
        // resources in a lazy pass that produced no newer layout revision, so
        // no layout commit re-ran the probe. Both completion branches must.
        string body = MethodBody(ReadSource("Controls", "MarkdownRendererControl.cs"), "private void CompleteLazyLayoutRealization(");
        int noRevision = body.IndexOf("if (result.Commit.LayoutRevision <= _appliedLazyLayoutRevision)", StringComparison.Ordinal);
        Assert.True(noRevision >= 0);
        int branchEnd = body.IndexOf("return;", noRevision, StringComparison.Ordinal);
        Assert.Contains("ScheduleVisibleDeferredScenes();", body[noRevision..branchEnd], StringComparison.Ordinal);
        Assert.Contains("ApplyLazyLayoutCommit(", body[branchEnd..], StringComparison.Ordinal);
        string commit = MethodBody(ReadSource("Controls", "MarkdownRendererControl.cs"), "private void ApplyLazyLayoutCommit(");
        Assert.Contains("ScheduleVisibleCodeBlockHighlighting();", commit, StringComparison.Ordinal);
    }

    private static string ReadSource(string folder, string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", folder, fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
