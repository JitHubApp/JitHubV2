using System.IO;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SessionlessImageRelayoutHandoffTests
{
    [Fact]
    public void ProcessQueuedImageRelayoutStagesBoundedSharedHandoffAndPreservesPendingRemainder()
    {
        string source = ReadControlSource();
        string process = ReadRegion(
            source,
            "private void ProcessQueuedImageRelayout(long ticket)",
            "private void InvalidateAutomationLayout()");

        Assert.DoesNotContain("if (PerformanceSession is not null)", process, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.RelayoutChangedBlocks(", process, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyLazyLayoutCommit(snapshot, anchor)", process, StringComparison.Ordinal);
        Assert.Contains(
            "int[] changed = ImageRelayoutBatch.CapturePendingIndices(",
            process,
            StringComparison.Ordinal);
        Assert.Contains("_pendingImageRelayoutBlockIndices,\n                optedIn: session is not null);", process, StringComparison.Ordinal);
        Assert.Contains(
            "snapshot.TryCaptureScrollAnchor(scroll.VerticalOffset, out var captured)",
            process,
            StringComparison.Ordinal);
        Assert.Contains("_imageRelayoutPayload = payload;", process, StringComparison.Ordinal);
        Assert.Contains("snapshot, changed, (float)Math.Max(50, ActualWidth)", process, StringComparison.Ordinal);
        Assert.Contains("_imageRelayoutTask = RunImageRelayoutAsync(", process, StringComparison.Ordinal);
        Assert.Contains("PerformanceSession as IMarkdownPerformanceSessionInternal", process, StringComparison.Ordinal);
        Assert.Contains("foreach (int completedBlockIndex in changed)", process, StringComparison.Ordinal);
        Assert.Contains("_pendingImageRelayoutBlockIndices.Remove(completedBlockIndex);", process, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingImageRelayoutBlockIndices.Clear();", process, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingImageRelayoutBlockIndices.RemoveWhere(", process, StringComparison.Ordinal);
        AssertOrdered(
            process,
            "_imageRelayoutTask = RunImageRelayoutAsync(",
            "foreach (int completedBlockIndex in changed)",
            "_pendingImageRelayoutBlockIndices.Remove(completedBlockIndex);");
        Assert.Contains("RetireCancellationTokenSource(cancellation, _imageRelayoutTask);", process, StringComparison.Ordinal);
        Assert.Contains("RequestRebuild();", process, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedHandoffUsesTaskRunForNullSessionAndPublishesOnlyAfterWorkDrain()
    {
        string source = ReadControlSource();
        string worker = ReadRegion(
            source,
            "private static async Task RunImageRelayoutAsync(",
            "private static WeakSnapshotPublication<LayoutSnapshot> CreateImageRelayoutPublication(");

        AssertOrdered(
            worker,
            "session is null",
            "Task.Run(() => layoutWork(cancellation.Token), CancellationToken.None)",
            "session.ScheduleLayoutWorkAsync(",
            "await layoutTask.ConfigureAwait(false)",
            "CreateImageRelayoutPublication(",
            "payload.TryOwnPublication(publication)",
            "payload.ReleaseAfterWorkDrains();",
            "dispatcher?.TryEnqueue(payload.PublishAfterWorkDrains)");
        Assert.Contains("WeakReference<MarkdownRendererControl> receiver", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownRendererControl control = this", worker, StringComparison.Ordinal);
        Assert.Contains("payload.ReleaseAfterWorkDrains();\n            payload = null!;", worker, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        int previous = -1;
        foreach (string fragment in fragments)
        {
            int current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{fragment}' after the preceding handoff stage.");
            previous = current;
        }
    }

    private static string ReadRegion(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source member '{start}'.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find the end of source member '{start}'.");
        return source[startIndex..endIndex];
    }

    private static string ReadControlSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string[] candidates =
            [
                Path.Combine(directory.FullName, "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"),
                Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs"),
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs from the test output.");
    }
}
