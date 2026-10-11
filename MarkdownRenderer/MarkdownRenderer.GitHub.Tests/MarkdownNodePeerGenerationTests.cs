using System;
using System.IO;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Verifies the non-retaining semantic-snapshot identity fence used by child
/// UIA peers. Source checks cover wiring; these are not live WinUI/UIA tests.
/// </summary>
public sealed class MarkdownNodePeerGenerationTests
{
    [Fact]
    public void RetainedChildPeerIsInvalidAfterSnapshotReplacementEvenWhenTextGenerationIsStable()
    {
        using var firstSnapshot = CreateSnapshot("same source", width: 240);
        using var relayoutSnapshot = CreateSnapshot("same source", width: 480);
        firstSnapshot.AssignAutomationDocumentGeneration(7);
        relayoutSnapshot.AssignAutomationDocumentGeneration(7);

        var capturedDocument = new WeakReference<MarkdownSemanticDocument>(firstSnapshot.SemanticDocument);

        Assert.True(MarkdownAutomationPeerLifetime.IsCurrentSnapshot(
            7,
            7,
            capturedDocument,
            firstSnapshot.SemanticDocument));
        Assert.False(MarkdownAutomationPeerLifetime.IsCurrentSnapshot(
            7,
            7,
            capturedDocument,
            relayoutSnapshot.SemanticDocument));
        Assert.False(MarkdownAutomationPeerLifetime.IsCurrentSnapshot(
            7,
            8,
            capturedDocument,
            firstSnapshot.SemanticDocument));
    }

    [Fact]
    public void ChildPeerCapturesWeakSnapshotIdentityAndFencesActionsAndPatterns()
    {
        string nodePeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownNodePeer.cs");
        string lifetime = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeerLifetime.cs");
        string blockPeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownBlockPeer.cs");
        string linkPeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkPeer.cs");
        string imagePeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs");
        string copyPeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownCodeBlockCopyPeer.cs");
        string automationPeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");

        Assert.Contains("MarkdownAutomationPeerLifetime.Capture(owner, root)", nodePeer, StringComparison.Ordinal);
        Assert.Contains("WeakReference<MarkdownSemanticDocument> _semanticDocument", lifetime, StringComparison.Ordinal);
        Assert.Contains("root.GetCurrentTextRangeDocument(out long generation)", lifetime, StringComparison.Ordinal);
        Assert.Contains("MarkdownAutomationDocumentGeneration.EnsureCurrent(", lifetime, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(capturedDocument, currentDocument)", lifetime, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(MarkdownAutomationPeerLifetime).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(LayoutSnapshot));
        Assert.DoesNotContain(typeof(MarkdownAutomationPeerLifetime).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(MarkdownSemanticDocument));

        AssertGuardedMembers(nodePeer,
        [
            "GetChildrenCore()",
            "GetPatternCore(PatternInterface patternIinterface)",
            "GetBoundingRectangleCore()",
            "IGridProvider.GetItem(int row, int column)",
            "ITableProvider.GetColumnHeaders()",
            "ITableItemProvider.GetColumnHeaderItems()",
            "IScrollProvider.Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)",
            "IScrollProvider.SetScrollPercent(double horizontalPercent, double verticalPercent)",
            "IInvokeProvider.Invoke()",
            "IToggleProvider.Toggle()",
            "SetFocusCore()",
        ]);

        AssertGuardedMembers(blockPeer,
        [
            "DocumentRange",
            "GetChildrenCore()",
            "GetPatternCore(PatternInterface patternIinterface)",
            "GetBoundingRectangleCore()",
            "GetSelection()",
            "GetVisibleRanges()",
            "RangeFromChild(IRawElementProviderSimple childElement)",
            "RangeFromPoint(Windows.Foundation.Point screenLocation)",
            "RangeFromAnnotation(IRawElementProviderSimple annotationElement)",
            "GetCaretRange(out bool isActive)",
        ]);
        AssertGuardedMembers(linkPeer,
        [
            "GetPatternCore(PatternInterface patternIinterface)",
            "Invoke()",
            "Expand()",
            "Collapse()",
            "GetBoundingRectangleCore()",
        ]);
        AssertGuardedMembers(imagePeer,
        [
            "GetPatternCore(PatternInterface patternInterface)",
            "Invoke()",
            "GetBoundingRectangleCore()",
        ]);
        AssertGuardedMembers(copyPeer,
        [
            "GetPatternCore(PatternInterface patternInterface)",
            "Invoke()",
            "GetBoundingRectangleCore()",
        ]);

        string notification = GetMemberBody(nodePeer, "NotifyImageStatusChanged()");
        Assert.Contains("_lifetime.TryGetCurrentPayload(_nodeReference", notification, StringComparison.Ordinal);
        Assert.Contains("return;", notification, StringComparison.Ordinal);

        string imageNotification = GetMemberBody(imagePeer, "NotifyImageStatusChanged()");
        Assert.Contains("_lifetime.TryGetCurrentPayload(_runReference", imageNotification, StringComparison.Ordinal);
        Assert.Contains("return;", imageNotification, StringComparison.Ordinal);

        string payloadLookup = GetMemberBody(lifetime, "TryGetCurrentPayload<T>(WeakReference<T> payload, out T? target)");
        Assert.Contains("if (!IsCurrent())", payloadLookup, StringComparison.Ordinal);
        Assert.Contains("return false;", payloadLookup, StringComparison.Ordinal);
        Assert.Contains("payload.TryGetTarget(out target)", payloadLookup, StringComparison.Ordinal);

        string documentLookup = GetMemberBody(automationPeer, "GetSemanticDocumentOrNull(LayoutSnapshot? snap)");
        Assert.Equal(2, CountOccurrences(documentLookup, "_peerCache.Clear();"));
        Assert.Equal(2, CountOccurrences(
            documentLookup,
            "_nodePeerCache = new Dictionary<MarkdownSemanticNode, MarkdownNodePeer>();"));
        Assert.Equal(2, CountOccurrences(documentLookup, "_owner.InvalidateAutomationChildPeerCaches();"));

        string invalidateChildCaches = GetMemberBody(control, "InvalidateAutomationChildPeerCaches()");
        Assert.Contains("_linkPeerCache.Clear();", invalidateChildCaches, StringComparison.Ordinal);
        Assert.Contains("_linkedImagePeerCache.Clear();", invalidateChildCaches, StringComparison.Ordinal);
    }

    [Fact]
    public void TextRangesRemainBoundToLogicalGenerationRatherThanSnapshotObject()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownTextRangeProvider.cs");
        Assert.Contains("private readonly long _documentGeneration;", source, StringComparison.Ordinal);
        Assert.Contains("_peer.GetTextRangeDocument(_documentGeneration)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WeakReference<MarkdownSemanticDocument>", source, StringComparison.Ordinal);
    }

    private static LayoutSnapshot CreateSnapshot(string source, float width) => new(
        Array.Empty<BlockBox>(),
        new MarkdownSourceMap(source),
        width,
        height: 0);

    private static string GetMemberBody(string source, string memberSignature)
    {
        int member = source.IndexOf(memberSignature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find member '{memberSignature}'.");
        int bodyStart = source.IndexOf('{', member + memberSignature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{memberSignature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{memberSignature}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        for (int offset = 0;
             (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
             offset += value.Length)
        {
            count++;
        }

        return count;
    }

    private static void AssertGuardedMembers(string source, string[] memberSignatures)
    {
        foreach (string member in memberSignatures)
        {
            string body = GetMemberBody(source, member);
            Assert.Contains("EnsureCurrentDocument();", body, StringComparison.Ordinal);
        }
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate repository source '{string.Join(Path.DirectorySeparatorChar, pathParts)}'.");
    }
}
