using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A context menu opens only for the snapshot, generation and layout
/// revision it was asked for: a gesture's menu is checked after its snapshot
/// lease ends, and a keyboard menu captures the published tuple at entry.
/// </summary>
public sealed class ContextMenuSnapshotFenceTests
{
    [Fact]
    public void KeyboardMenuCapturesThePublishedTuple()
    {
        using var fixture = new ImageRelayoutFixture();

        var fence = ContextMenuSnapshotFence.For(fixture.Published, requiredSnapshot: null, 99, 99);

        Assert.Same(fixture.Snapshot, fence.Snapshot);
        Assert.Equal(fixture.Published.Generation, fence.Generation);
        Assert.Equal(fixture.Snapshot.LayoutRevision, fence.LayoutRevision);
        Assert.True(fence.IsCurrent(fixture.Lifetime, fixture.Published));
    }

    [Fact]
    public void GestureMenuKeepsTheTupleItHitTested()
    {
        using var fixture = new ImageRelayoutFixture();

        var fence = ContextMenuSnapshotFence.For(fixture.Published, fixture.Snapshot, 5, 3);

        Assert.Equal(new ContextMenuSnapshotFence(fixture.Snapshot, 5, 3), fence);
        Assert.False(fence.IsCurrent(fixture.Lifetime, fixture.Published));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("snapshot")]
    [InlineData("unloaded")]
    [InlineData("disposed")]
    public void FenceIsStaleOnceItsTupleOrTheViewChanges(string change)
    {
        using var fixture = new ImageRelayoutFixture();
        var fence = ContextMenuSnapshotFence.For(fixture.Published, requiredSnapshot: null, 0, 0);
        if (change == "revision")
            fence = fence with { LayoutRevision = fence.LayoutRevision + 1 };
        else
            Mutate(fixture, change);

        Assert.False(fence.IsCurrent(fixture.Lifetime, fixture.Published));
    }

    [Fact]
    public void FenceWithoutASnapshotIgnoresTheRevision()
    {
        var lifetime = new MarkdownViewLifetime();
        lifetime.MarkLoaded();
        var published = new PublishedLayout { Generation = 4 };

        var fence = ContextMenuSnapshotFence.For(published, requiredSnapshot: null, 0, 0);

        Assert.Equal(new ContextMenuSnapshotFence(null, 4, 0), fence);
        Assert.True(fence.IsCurrent(lifetime, published));
    }

    [Fact]
    public void GestureWithoutAMenuKeepsItsOwnResult()
    {
        using var fixture = new ImageRelayoutFixture();
        var menu = new RecordingMenu();
        var completion = new GestureContextMenuCompletion(fixture.Lifetime, fixture.Published, menu);
        var input = new PointerInput { Handled = true };

        Assert.True(completion.Complete(input, fixture.Snapshot, fixture.Published.Generation, fixture.Snapshot.LayoutRevision));
        Assert.Empty(menu.Shows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentGestureOpensTheMenuForItsExactTuple(bool touchSelection)
    {
        using var fixture = new ImageRelayoutFixture();
        var menu = new RecordingMenu { Opens = true };
        var completion = new GestureContextMenuCompletion(fixture.Lifetime, fixture.Published, menu);
        long revision = fixture.Snapshot.LayoutRevision;
        PointerInput input = MenuGesture(revision, touchSelection);

        Assert.True(completion.Complete(input, fixture.Snapshot, fixture.Published.Generation, revision));

        Assert.Equal(
            [(new Point(12, 34), touchSelection, fixture.Snapshot, fixture.Published.Generation, revision)],
            menu.Shows);
    }

    [Fact]
    public void MenuThatDoesNotOpenLeavesTheGestureUnhandled()
    {
        using var fixture = new ImageRelayoutFixture();
        var menu = new RecordingMenu { Opens = false };
        var completion = new GestureContextMenuCompletion(fixture.Lifetime, fixture.Published, menu);
        long revision = fixture.Snapshot.LayoutRevision;

        Assert.False(completion.Complete(MenuGesture(revision, false), fixture.Snapshot, fixture.Published.Generation, revision));
        Assert.Single(menu.Shows);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("gesture revision")]
    [InlineData("snapshot")]
    [InlineData("unloaded")]
    [InlineData("disposed")]
    public void StaleGestureNeverOpensTheMenu(string change)
    {
        using var fixture = new ImageRelayoutFixture();
        var menu = new RecordingMenu { Opens = true };
        var completion = new GestureContextMenuCompletion(fixture.Lifetime, fixture.Published, menu);
        long generation = fixture.Published.Generation;
        long revision = fixture.Snapshot.LayoutRevision;
        PointerInput input = MenuGesture(change == "gesture revision" ? revision + 1 : revision, false);
        if (change == "revision")
        {
            // The gesture and the caller agree on a revision the snapshot no longer has.
            input.GestureLayoutRevision = ++revision;
        }
        else if (change != "gesture revision")
        {
            Mutate(fixture, change);
        }

        Assert.False(completion.Complete(input, fixture.Snapshot, generation, revision));
        Assert.Empty(menu.Shows);
    }

    private static PointerInput MenuGesture(long gestureLayoutRevision, bool touchSelection) => new()
    {
        ShowContextMenuAfterInteraction = true,
        TouchSelectionContextMenu = touchSelection,
        GesturePosition = new Point(12, 34),
        GestureLayoutRevision = gestureLayoutRevision,
        Handled = true,
    };

    private static void Mutate(ImageRelayoutFixture fixture, string change)
    {
        switch (change)
        {
            case "generation":
                fixture.Published.Generation++;
                break;
            case "snapshot":
                fixture.Published.Snapshot = null;
                break;
            case "unloaded":
                fixture.Lifetime.MarkUnloaded();
                break;
            default:
                fixture.Lifetime.MarkDisposing();
                break;
        }
    }

    private sealed class RecordingMenu : IContextMenuOpener
    {
        internal bool Opens { get; init; }

        internal List<(Point Point, bool Touch, LayoutSnapshot? Snapshot, long Generation, long Revision)> Shows { get; } = [];

        public bool Show(
            Point placementPoint,
            bool touchSelection,
            LayoutSnapshot? requiredSnapshot,
            long requiredSnapshotGeneration,
            long requiredLayoutRevision)
        {
            Shows.Add((placementPoint, touchSelection, requiredSnapshot, requiredSnapshotGeneration, requiredLayoutRevision));
            return Opens;
        }
    }
}
