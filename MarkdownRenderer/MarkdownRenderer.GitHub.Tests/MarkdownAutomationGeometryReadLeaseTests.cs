using System.IO;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-wiring regression tests for UIA geometry reads. The production peer
/// is a WinUI AutomationPeer and cannot be safely instantiated by the
/// headless contract suite; LayoutSnapshot lease admission itself is tested
/// independently by LayoutSnapshotMeasuredReadLeaseTests.
/// </summary>
public sealed class MarkdownAutomationGeometryReadLeaseTests
{
    [Fact]
    public void GeometryReaderUsesNonblockingLeaseAndFencesSnapshotAndGenerationAroundCopy()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string reader = GetMethodBody(source, "TryReadLayoutGeometry<TState, T>(");
        string identityCheck = GetMethodBody(source, "private bool IsCurrentLayoutDocument(");

        Assert.Contains("snapshot.TryBeginInteraction()", reader, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndInteraction()", reader, StringComparison.Ordinal);
        Assert.Contains("finally", reader, StringComparison.Ordinal);
        string compactReader = reader.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(
            compactReader,
            "IsCurrentLayoutDocument(snapshot,expectedDocument,expectedDocumentGeneration)"));
        Assert.DoesNotContain("Monitor.Enter", reader, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(currentSnapshot, snapshot)", identityCheck, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_semanticDocument, expectedDocument)", identityCheck, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(snapshot.SemanticDocument, expectedDocument)", identityCheck, StringComparison.Ordinal);
        Assert.Contains("MarkdownAutomationDocumentGeneration.EnsureCurrent", identityCheck, StringComparison.Ordinal);
        string visibleRanges = GetMethodBody(source, "GetVisibleRanges()");
        string rangeFromPoint = GetMethodBody(source, "RangeFromPoint(Windows.Foundation.Point screenLocation)");
        Assert.Contains("new MarkdownTextRangeProvider(this, 0, 0, null, generation, doc.Text.Length)", visibleRanges, StringComparison.Ordinal);
        Assert.Contains("TryReadLayoutGeometry", rangeFromPoint, StringComparison.Ordinal);
        Assert.Contains("new MarkdownTextRangeProvider(this, 0, 0, null, generation, doc.Text.Length)", rangeFromPoint, StringComparison.Ordinal);
    }

    [Fact]
    public void RangeFromPointCoercionAndHitTestingRunInsideTheLeaseCallback()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string method = GetMethodBody(source, "RangeFromPoint(Windows.Foundation.Point screenLocation)");

        int lease = method.IndexOf("TryReadLayoutGeometry", StringComparison.Ordinal);
        int coerce = method.IndexOf("TryCoercePointToNearestTextRect", StringComparison.Ordinal);
        int vectorHitTest = method.IndexOf("TryHitTestVectorText", StringComparison.Ordinal);
        int hitTest = method.IndexOf("snapshot.HitTest", StringComparison.Ordinal);
        int projection = method.IndexOf("TextOffsetFromDocumentPosition(position)", StringComparison.Ordinal);
        Assert.True(lease >= 0 && coerce > lease && vectorHitTest > coerce && hitTest > vectorHitTest && projection > hitTest);
        Assert.DoesNotContain("TryCoercePointToNearestTextRect(rawDocumentPoint", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RangeGeometryConsumesMutableBoxEnumerationsBeforeReleasingLease()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownTextRangeProvider.cs");
        string bounding = GetMethodBody(source, "GetBoundingRectangles(out double[] boundingRectangles)");
        string scroll = GetMethodBody(source, "ScrollIntoView(bool alignToTop)");

        Assert.Contains("(Document: doc, Range: this)", bounding, StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) => state.Document.GetDocumentRects(", bounding, StringComparison.Ordinal);
        Assert.Contains("placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint", bounding, StringComparison.Ordinal);
        Assert.Contains("placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint", bounding, StringComparison.Ordinal);
        Assert.Contains("state.Range._start", bounding, StringComparison.Ordinal);
        Assert.Contains("state.Range._end", bounding, StringComparison.Ordinal);
        Assert.Contains("Array.Empty<double>()", bounding, StringComparison.Ordinal);
        Assert.True(bounding.IndexOf("ToArray()", StringComparison.Ordinal) <
                    bounding.IndexOf("GetScreenRectForDocumentRect", StringComparison.Ordinal));
        Assert.Contains("(Document: doc, Range: this)", scroll, StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) => state.Document.GetDocumentRects(", scroll, StringComparison.Ordinal);
        Assert.Contains("placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint", scroll, StringComparison.Ordinal);
        Assert.Contains("placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint", scroll, StringComparison.Ordinal);
        Assert.Contains("expandDegenerate: true", scroll, StringComparison.Ordinal);
        Assert.True(scroll.IndexOf("ToArray()", StringComparison.Ordinal) <
                    scroll.IndexOf("ScrollDocumentRectIntoView", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticAndInlineChildBoundsUseTheSameSnapshotLease()
    {
        string root = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string node = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownNodePeer.cs");
        string block = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownBlockPeer.cs");
        string link = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkPeer.cs");
        string linkedImage = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs");
        string copy = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownCodeBlockCopyPeer.cs");

        string visibleRanges = GetMethodBody(root, "GetVisibleRanges()");
        Assert.Contains("GetVisibleSpanBounds(span, snapshot)", visibleRanges, StringComparison.Ordinal);
        Assert.Contains("(Document: doc, Viewport: viewport)", visibleRanges, StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) =>", visibleRanges, StringComparison.Ordinal);
        int visibleLease = visibleRanges.IndexOf("TryReadLayoutGeometry", StringComparison.Ordinal);
        int spanBounds = visibleRanges.IndexOf("GetVisibleSpanBounds(span, snapshot)", StringComparison.Ordinal);
        int rangeSummary = visibleRanges.IndexOf("return (Start: visibleStart ?? 0, End: visibleEnd ?? 0)", StringComparison.Ordinal);
        Assert.True(visibleLease >= 0 && spanBounds > visibleLease && rangeSummary > spanBounds);
        Assert.Contains("snapshot.ToLogicalRectForBlockIndexUnderPaint(span.LayoutBlockIndex, bounds)",
            GetMethodBody(root, "private static Windows.Foundation.Rect GetVisibleSpanBounds("), StringComparison.Ordinal);
        Assert.DoesNotContain("new (int Start, int End, Windows.Foundation.Rect Bounds)[doc.TextSpans.Count]", visibleRanges, StringComparison.Ordinal);
        Assert.Contains("TryReadNodeBounds(", GetMethodBody(node, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("TryReadLayoutGeometry(", GetMethodBody(block, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("TryReadLayoutGeometry(", GetMethodBody(link, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("TryReadLayoutGeometry(", GetMethodBody(linkedImage, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("TryReadLayoutGeometry(", GetMethodBody(copy, "GetBoundingRectangleCore()"), StringComparison.Ordinal);

        Assert.DoesNotContain("_node.Bounds", GetMethodBody(node, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.DoesNotContain("_box.Bounds", GetMethodBody(block, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.DoesNotContain("_parent.Box.GetRunRect", GetMethodBody(link, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.DoesNotContain("_parent.Box.GetRunRect", GetMethodBody(linkedImage, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
    }

    [Fact]
    public void GeometryReadersUseStaticCallbacksWithExplicitStateInsteadOfCapturingPerQueryClosures()
    {
        string root = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string range = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownTextRangeProvider.cs");
        string block = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownBlockPeer.cs");
        string link = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkPeer.cs");
        string linkedImage = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs");
        string copy = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownCodeBlockCopyPeer.cs");

        string reader = GetMethodBody(root, "TryReadLayoutGeometry<TState, T>(");
        int signatureStart = root.IndexOf("TryReadLayoutGeometry<TState, T>(", StringComparison.Ordinal);
        int bodyStart = root.IndexOf('{', signatureStart);
        string signature = root[signatureStart..bodyStart];
        Assert.Contains("TState state", signature, StringComparison.Ordinal);
        Assert.Contains("Func<LayoutSnapshot, TState, T> read", signature, StringComparison.Ordinal);
        Assert.Contains("candidate = read(snapshot, state)", reader, StringComparison.Ordinal);

        Assert.Contains("static (snapshot, state) =>", GetMethodBody(root, "RangeFromPoint(Windows.Foundation.Point screenLocation)"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) =>", GetMethodBody(range, "GetBoundingRectangles(out double[] boundingRectangles)"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) =>", GetMethodBody(range, "ScrollIntoView(bool alignToTop)"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) =>", GetMethodBody(link, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, state) =>", GetMethodBody(linkedImage, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, currentBox) =>", GetMethodBody(block, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, currentBox) =>", GetMethodBody(copy, "GetBoundingRectangleCore()"), StringComparison.Ordinal);
        Assert.Contains("static (snapshot, currentNode) =>", root, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildGeometryRetainsItsCapturedSemanticGenerationAndTransformsAfterLease()
    {
        string lifetime = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeerLifetime.cs");
        string node = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownNodePeer.cs");
        string block = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownBlockPeer.cs");
        string link = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkPeer.cs");
        string linkedImage = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs");
        string copy = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownCodeBlockCopyPeer.cs");

        Assert.Contains("DocumentGeneration => _documentGeneration", lifetime, StringComparison.Ordinal);
        Assert.Contains("GetCurrentSemanticDocument()", lifetime, StringComparison.Ordinal);
        foreach (string source in new[] { node, block, link, linkedImage, copy })
        {
            string bounds = GetMethodBody(source, "GetBoundingRectangleCore()");
            int read = bounds.IndexOf("TryRead", StringComparison.Ordinal);
            int transform = bounds.IndexOf("GetScreenRectForDocumentRect", StringComparison.Ordinal);
            Assert.True(read >= 0 && transform > read);
            Assert.Contains("DocumentGeneration", bounds, StringComparison.Ordinal);
        }
    }

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
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
            $"Could not locate repository source '{Path.Combine(pathParts)}' from '{AppContext.BaseDirectory}'.");
    }
}
