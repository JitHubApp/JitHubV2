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

    private static LayoutSnapshot CreateSnapshot(string source, float width) => new(
        Array.Empty<BlockBox>(),
        new MarkdownSourceMap(source),
        width,
        height: 0);
}
