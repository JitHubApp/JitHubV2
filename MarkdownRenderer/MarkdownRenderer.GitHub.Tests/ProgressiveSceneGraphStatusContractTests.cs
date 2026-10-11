using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// Source wiring checks complement native-box/policy behavior tests. These are
// not substitutes for the required live UIA property-event qualification.
public sealed class ProgressiveSceneGraphStatusContractTests
{
    [Fact]
    public void NewAndAlreadyAppliedLazyRevisionsAndFailureDrainStatusChanges()
    {
        string source = Read("Controls", "MarkdownRendererControl.cs");
        string completion = Section(source, "private void CompleteLazyLayoutRealization(",
            "private void ApplyLazyLayoutCommit(");
        string failure = Section(completion, "if (failure is not null)",
            "if (result.Commit.LayoutRevision <= _appliedLazyLayoutRevision)");
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged();", failure);
        int applied = completion.IndexOf("ApplyLazyLayoutCommit(snapshot,", StringComparison.Ordinal);
        Assert.True(applied >= 0);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged();", completion[..applied]);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged();", completion[applied..]);
    }

    [Fact]
    public void BusyLeaseRetriesAndDrainsWithoutRequiringAnAutomationListener()
    {
        string source = Read("Controls", "MarkdownRendererControl.cs");
        string notification = Section(source, "internal void NotifyVisibleDeferredSceneItemStatusMayHaveChanged()",
            "private void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()");
        Assert.Contains("Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 1)", notification);
        Assert.Contains("policy.ClearGraphStatusChanges();", notification);
        Assert.Contains("finally", notification);
        string afterPaint = Section(source, "private void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()",
            "internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadinessForAutomation(");
        Assert.DoesNotContain("ListenerExists", afterPaint);
        Assert.Contains("NotifyVisibleDeferredSceneItemStatusMayHaveChanged();", afterPaint);
    }

    [Fact]
    public void RootStatusUsesOneValidatedReadinessProbeAndFailureStaysNotReady()
    {
        string peer = Read("Accessibility", "MarkdownAutomationPeer.cs");
        string status = Section(peer, "protected override string GetItemStatusCore()",
            "protected override IList<AutomationPeer> GetChildrenCore()");
        Assert.Equal(2, status.Split("GetVisibleDeferredSceneStatusForAutomation()",
            StringSplitOptions.None).Length);
        Assert.Contains("sceneStatus == MarkdownVisibleSceneReadiness.Failed", status);
        Assert.Contains("sceneStatus != MarkdownVisibleSceneReadiness.Ready", status);
        string control = Read("Controls", "MarkdownRendererControl.cs");
        Assert.DoesNotContain("IsVisibleDeferredSceneReadyForAutomation(", control);
    }

    [Fact]
    public void ScenePeerStatusNotificationUsesSceneIndexNotDocumentTraversal()
    {
        string source = Read("Accessibility", "MarkdownAutomationPeer.cs");
        string notification = Section(source, "internal void NotifyVectorSceneStatusChanged(VectorSceneBox box)",
            "internal void NotifyDocumentChanged()");
        Assert.Contains("_vectorPeerCache.TryGetValue(box", notification);
        Assert.DoesNotContain("_nodePeerCache", notification);
        Assert.DoesNotContain("EnumerateSemanticNodes", notification);
        Assert.Equal(2, source.Split("_vectorPeerCache.Clear();", StringSplitOptions.None).Length - 1);
    }

    private static string Section(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing method start: {start}");
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Missing method end: {end}");
        return source[first..last];
    }

    private static string Read(string directory, string file)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string root = Path.Combine(current.FullName, "MarkdownRenderer", "MarkdownRenderer");
            if (File.Exists(Path.Combine(root, "MarkdownRenderer.csproj")))
                return File.ReadAllText(Path.Combine(root, directory, file));
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("MarkdownRenderer source root was not found.");
    }
}
