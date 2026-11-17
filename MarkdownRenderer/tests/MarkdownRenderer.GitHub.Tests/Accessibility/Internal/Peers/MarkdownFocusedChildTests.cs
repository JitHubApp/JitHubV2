using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Accessibility.Internal.Peers;

/// <summary>
/// Which automation peer answers a UI Automation focused-element query while
/// the renderer holds keyboard focus. A painted link, linked image, vector
/// semantic or overflow region that has virtual keyboard focus must resolve to
/// its own child peer, so Narrator announces the link rather than the whole
/// document. Items with a real XAML element, such as the code-block copy
/// button, resolve to no virtual child so that element's own peer answers.
/// </summary>
public sealed class MarkdownFocusedChildTests
{
    private const string TwoLinks =
        "Read [the guide](https://example.test/guide) and [the FAQ](https://example.test/faq).";

    [Fact]
    public async Task FocusedLinkResolvesToItsLinkPeer()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TwoLinks);
        KeyboardFocusModel focus = CreateFocus(snapshot);
        int first = IndexOfLink(focus, snapshot, "the guide");

        focus.FocusItem(first);
        ResolvedPeer resolved = Resolve(focus.GetFocusedChild());

        Assert.Equal("link", resolved.Kind);
        LinkRun link = Assert.IsType<LinkRun>(resolved.Run);
        Assert.Equal("the guide", link.AccessibleText);
        Assert.Same(OwnerOf(snapshot, link), resolved.Inline);
    }

    [Fact]
    public async Task MovingFocusToTheNextLinkResolvesToThatLinksPeer()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TwoLinks);
        KeyboardFocusModel focus = CreateFocus(snapshot);
        focus.FocusItem(IndexOfLink(focus, snapshot, "the guide"));
        ResolvedPeer before = Resolve(focus.GetFocusedChild());

        focus.FocusItem(IndexOfLink(focus, snapshot, "the FAQ"));
        ResolvedPeer after = Resolve(focus.GetFocusedChild());

        Assert.NotSame(before.Run, after.Run);
        Assert.Equal("the FAQ", Assert.IsType<LinkRun>(after.Run).AccessibleText);
    }

    [Fact]
    public async Task FocusedLinkedImageResolvesToItsLinkedImagePeer()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(
            "[![Build status](https://example.test/badge.svg)](https://example.test/ci)");
        KeyboardFocusModel focus = CreateFocus(snapshot);
        FocusableItem item = Assert.Single(focus.Items!, static candidate => candidate.IsLink);

        focus.FocusItem(IndexOf(focus, item));
        ResolvedPeer resolved = Resolve(focus.GetFocusedChild());

        Assert.Equal("linked-image", resolved.Kind);
        InlineImageRun image = Assert.IsType<InlineImageRun>(resolved.Run);
        Assert.True(image.IsLinked);
        Assert.Equal("Build status", image.AltText);
    }

    [Fact]
    public async Task WithoutAFocusedItemTheDocumentKeepsFocus()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TwoLinks);
        KeyboardFocusModel focus = CreateFocus(snapshot);
        focus.FocusItem(IndexOfLink(focus, snapshot, "the guide"));

        focus.ClearFocus();
        MarkdownFocusedChild child = focus.GetFocusedChild();

        Assert.True(child.IsNone);
        Assert.Null(child.ResolvePeer(new RecordingPeers()));
    }

    [Fact]
    public async Task CodeBlockCopyButtonIsNotAVirtualChild()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(
            "```csharp\nvar answer = 42;\n```");
        KeyboardFocusModel focus = CreateFocus(snapshot);
        FocusableItem copy = Assert.Single(focus.Items!, static candidate => candidate.IsCodeBlockCopy);

        focus.FocusItem(IndexOf(focus, copy));

        Assert.True(focus.GetFocusedChild().IsNone);
    }

    private static KeyboardFocusModel CreateFocus(LayoutSnapshot snapshot)
    {
        var published = new PublishedLayout { Snapshot = snapshot, Generation = 1 };
        var focus = new KeyboardFocusModel(new MarkdownViewLifetime(), published);
        focus.ReplaceItems(snapshot.CollectFocusableItems());
        return focus;
    }

    private static int IndexOfLink(KeyboardFocusModel focus, LayoutSnapshot snapshot, string text)
    {
        IReadOnlyList<FocusableItem> items = focus.Items!;
        for (int index = 0; index < items.Count; index++)
        {
            if (FocusableItemQueries.TryGetLinkedAtomicForFocusable(snapshot, items[index], out _, out InlineRun run) &&
                run is LinkRun link &&
                link.AccessibleText == text)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"No focusable link named '{text}'.");
    }

    private static int IndexOf(KeyboardFocusModel focus, FocusableItem item)
    {
        int index = focus.Items!.ToList().IndexOf(item);
        Assert.True(index >= 0);
        return index;
    }

    private static InlineContainerBox OwnerOf(LayoutSnapshot snapshot, InlineRun run) =>
        GitHubReadmeEngineLayoutFixture.EnumerateInlineBoxes(snapshot)
            .Single(inline => inline.Runs.Contains(run));

    private static ResolvedPeer Resolve(MarkdownFocusedChild child)
    {
        Assert.False(child.IsNone);
        ResolvedPeer? resolved = child.ResolvePeer(new RecordingPeers());
        Assert.NotNull(resolved);
        return resolved;
    }

    private sealed record ResolvedPeer(string Kind, InlineContainerBox? Inline, InlineRun? Run);

    private sealed class RecordingPeers : IMarkdownFocusedChildPeers<ResolvedPeer>
    {
        public ResolvedPeer? ForLink(InlineContainerBox inline, LinkRun link) => new("link", inline, link);

        public ResolvedPeer? ForLinkedImage(InlineContainerBox inline, InlineImageRun image) =>
            new("linked-image", inline, image);

        public ResolvedPeer? ForInlineVectorScene(InlineVectorSceneRun scene) =>
            new("inline-vector-scene", null, scene);

        public ResolvedPeer? ForVectorSemantic(VectorSceneBox box, int semanticIndex) =>
            new("vector-semantic", null, null);

        public ResolvedPeer? ForHorizontalOverflow(IHorizontalOverflowBox overflow) =>
            new("horizontal-overflow", null, null);
    }
}
