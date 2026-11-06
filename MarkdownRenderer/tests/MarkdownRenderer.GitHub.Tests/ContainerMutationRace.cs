using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerImageRelayoutHarness;
using ContainerMutation = MarkdownRenderer.GitHub.Tests.RootLocalContainerImageRelayoutTests.ContainerMutation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// One injected source mutation that races a root-local container's private
/// measurement. It seeds a hidden raw margin on a tight-list leaf, applies the
/// mutation from the before-measure hook, checks that the source kept the
/// mutation after the retry, and restores the source for teardown.
/// </summary>
internal sealed class ContainerMutationRace
{
    private readonly ContainerMutation _mutation;
    private readonly StackBox _list;
    private readonly ImageBox[] _images;
    private readonly InlineContainerBox _leaf;
    private readonly Thickness _unsuppressedRawMargin;
    private readonly Thickness _originalRawMargin;
    private readonly Thickness _originalEffectiveMargin;
    private readonly Thickness _mutatedRawMargin;
    private readonly Rect[] _originalImageBounds;
    private readonly FlowDirection _originalDirection;
    private readonly BlockBox[] _originalChildren;

    private ContainerMutationRace(
        ContainerMutation mutation,
        StackBox list,
        ImageBox[] images,
        InlineContainerBox leaf,
        Thickness unsuppressedRawMargin)
    {
        _mutation = mutation;
        _list = list;
        _images = images;
        _leaf = leaf;
        _unsuppressedRawMargin = unsuppressedRawMargin;
        _originalRawMargin = leaf.RawPlacementMargin;
        _originalEffectiveMargin = leaf.Margin;
        _mutatedRawMargin = new(_originalRawMargin.Left, _originalRawMargin.Top + 7,
            _originalRawMargin.Right, _originalRawMargin.Bottom + 3);
        _originalImageBounds = images.Select(static image => image.Bounds).ToArray();
        _originalDirection = list.FlowDirection;
        _originalChildren = list.Children.ToArray();
    }

    internal bool Mutated { get; private set; }

    /// <summary>
    /// Seeds a real hidden raw margin into the list's tight-list text leaf,
    /// establishes its measured source baseline, and records the originals the
    /// race mutates and later restores.
    /// </summary>
    internal static ContainerMutationRace Seed(
        LayoutSnapshot snapshot,
        StackBox list,
        ImageBox[] images,
        ContainerMutation mutation)
    {
        InlineContainerBox suppressedMarginLeaf = EnumerateBoxes([list])
            .OfType<InlineContainerBox>()
            .First(inline => !inline.UseContainerMargin &&
                inline.Runs.OfType<TextRun>().Any(static run =>
                    run.Text.Contains("nested text-only leaf", StringComparison.Ordinal)));
        // Tight-list paragraphs use explicit margins. Inject a real hidden raw margin
        // into that leaf, then establish its measured source baseline before the race.
        Assert.False(suppressedMarginLeaf.SuppressTopMargin);
        Assert.False(suppressedMarginLeaf.SuppressBottomMargin);
        Thickness unsuppressedRawMargin = suppressedMarginLeaf.RawPlacementMargin;
        Thickness seededRawMargin = new(
            unsuppressedRawMargin.Left + 1,
            unsuppressedRawMargin.Top + 7,
            unsuppressedRawMargin.Right + 1,
            unsuppressedRawMargin.Bottom + 5);
        suppressedMarginLeaf.Margin = seededRawMargin;
        suppressedMarginLeaf.SuppressTopMargin = true;
        suppressedMarginLeaf.SuppressBottomMargin = true;
        snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
        Assert.Equal(seededRawMargin, suppressedMarginLeaf.RawPlacementMargin);
        Assert.Equal(0, suppressedMarginLeaf.Margin.Top);
        Assert.Equal(0, suppressedMarginLeaf.Margin.Bottom);
        return new ContainerMutationRace(mutation, list, images, suppressedMarginLeaf, unsuppressedRawMargin);
    }

    internal void Apply()
    {
        Mutated = true;
        switch (_mutation)
        {
            case ContainerMutation.Margin:
                _leaf.Margin = _mutatedRawMargin;
                break;
            case ContainerMutation.ImageGeometry:
                _images[0].Arrange(
                    (float)_originalImageBounds[0].X + 5,
                    (float)_originalImageBounds[0].Y + 7,
                    (float)_originalImageBounds[0].Width);
                break;
            case ContainerMutation.LayoutConfiguration:
                _list.FlowDirection = _originalDirection == FlowDirection.LeftToRight
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                _list.InterChildSpacing += 11;
                break;
            case ContainerMutation.ChildOrder:
                BlockBox[] detached = _list.DetachChildrenForTransfer();
                foreach (BlockBox child in detached.Reverse())
                    _list.Add(child);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_mutation));
        }
    }

    internal void AssertSourceKeptMutation()
    {
        if (_mutation == ContainerMutation.Margin)
        {
            Assert.Equal(_mutatedRawMargin, _leaf.RawPlacementMargin);
            Assert.Equal(_originalEffectiveMargin, _leaf.Margin);
        }
        if (_mutation == ContainerMutation.LayoutConfiguration)
            Assert.NotEqual(_originalDirection, _list.FlowDirection);
        if (_mutation == ContainerMutation.ChildOrder)
            Assert.Equal(_originalChildren.Reverse(), _list.Children);
        if (_mutation == ContainerMutation.ImageGeometry)
        {
            Assert.Equal(_originalImageBounds[0].X + 5, _images[0].Bounds.X);
            Assert.Equal(_originalImageBounds[0].Y + 7, _images[0].Bounds.Y);
        }
    }

    /// <summary>
    /// Restores the source only after the fence has observed the injected race,
    /// then removes the seeded margin. This keeps teardown ownership valid even
    /// if an assertion failed.
    /// </summary>
    internal void Restore(LayoutSnapshot snapshot)
    {
        if (Mutated)
        {
            if (_mutation == ContainerMutation.Margin)
                _leaf.Margin = _originalRawMargin;
            else if (_mutation == ContainerMutation.ImageGeometry)
                _images[0].Arrange((float)_originalImageBounds[0].X,
                    (float)_originalImageBounds[0].Y, (float)_originalImageBounds[0].Width);
            else if (_mutation == ContainerMutation.LayoutConfiguration)
            {
                _list.FlowDirection = _originalDirection;
                _list.InterChildSpacing -= 11;
            }
            else if (_mutation == ContainerMutation.ChildOrder)
            {
                _ = _list.DetachChildrenForTransfer();
                foreach (BlockBox child in _originalChildren)
                    _list.Add(child);
            }
        }
        _leaf.SuppressTopMargin = false;
        _leaf.SuppressBottomMargin = false;
        _leaf.Margin = _unsuppressedRawMargin;
        snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
    }
}
