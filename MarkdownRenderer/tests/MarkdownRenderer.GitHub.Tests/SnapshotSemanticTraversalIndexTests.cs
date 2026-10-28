using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Protects the constructor-owned index collector when semantic construction
/// also observes boxes that are filtered from the accessibility tree.
/// </summary>
public sealed class SnapshotSemanticTraversalIndexTests
{
    [Fact]
    public void StandaloneSemanticBuildDoesNotMutateSnapshotIndexesOrFocusArrays()
    {
        TestFixture fixture = CreateDecorativeImageFixture();
        using LayoutSnapshot snapshot = new(
            fixture.Roots,
            fixture.Context.SourceMap,
            width: 360,
            height: 640);
        Dictionary<ImageBox, int> imageOwners = ReadField<Dictionary<ImageBox, int>>(
            snapshot,
            "_imageTopLevelOrdinalByIdentity");
        Assert.Equal(0, imageOwners[fixture.InlineDecorativeImage.Image]);
        Assert.Equal(1, imageOwners[fixture.StandaloneDecorativeImage]);
        SnapshotIndexState before = SnapshotIndexState.Capture(snapshot);
        Rect[] originalBounds = CaptureBounds(fixture);
        bool[] originalDirty = CaptureDirty(fixture);

        MarkdownSemanticDocument standalone = MarkdownSemanticDocument.Build(snapshot);

        before.AssertUnchanged(snapshot);
        Assert.Equal(originalBounds, CaptureBounds(fixture));
        Assert.Equal(originalDirty, CaptureDirty(fixture));
        Assert.Equal("visible link", standalone.Text);
        Assert.DoesNotContain(
            MarkdownSemanticDocument.EnumerateDepthFirst(standalone.Root),
            node => ReferenceEquals(node.ImageBox, fixture.InlineDecorativeImage.Image) ||
                ReferenceEquals(node.ImageBox, fixture.StandaloneDecorativeImage));
        Assert.Equal(
            new[]
            {
                new FocusableItem(fixture.Paragraph.BlockIndex, fixture.Link.InlineIndex, FocusableItemKind.Link),
            },
            snapshot.CollectFocusableItems());
    }

    [Fact]
    public void UnknownOverflowLeafRemainsIndexedAndFocusableButIsOmittedSemantically()
    {
        MarkdownLayoutContext context = CreateContext();
        var unknown = new TestUnknownOverflowLeaf { BlockIndex = 902 };
        var overflowOwner = new TestOverflowStackBox { BlockIndex = 901 };
        overflowOwner.Add(unknown);
        var roots = new BlockBox[] { overflowOwner };
        using LayoutSnapshot snapshot = new(roots, context.SourceMap, width: 360, height: 640);

        IReadOnlyDictionary<int, int> topLevel = ReadAssignableField<IReadOnlyDictionary<int, int>>(
            snapshot,
            "_topLevelOrdinalByBlockIndex");
        Assert.Equal(0, topLevel[overflowOwner.BlockIndex]);
        Assert.Equal(0, topLevel[unknown.BlockIndex]);

        HashSet<IHorizontalOverflowBox> overflowBoxes = ReadField<HashSet<IHorizontalOverflowBox>>(
            snapshot,
            "_horizontalOverflowBoxes");
        Assert.Contains(overflowOwner, overflowBoxes);
        Assert.Contains(unknown, overflowBoxes);
        Dictionary<int, IHorizontalOverflowBox> overflowByBlock = ReadField<Dictionary<int, IHorizontalOverflowBox>>(
            snapshot,
            "_horizontalOverflowByBlockIndex");
        Assert.Same(unknown, overflowByBlock[unknown.BlockIndex]);

        FocusPlanEntrySnapshot[] focusPlan = SnapshotIndexState.CaptureFocusablePlan(snapshot);
        Assert.Collection(
            focusPlan,
            item =>
            {
                Assert.Equal(new FocusableItem(unknown.BlockIndex, 0, FocusableItemKind.HorizontalOverflow), item.Item);
                Assert.Same(unknown, item.Overflow);
            });

        MarkdownSemanticDocument semantic = MarkdownSemanticDocument.Build(snapshot);
        Assert.DoesNotContain(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            node => ReferenceEquals(node.Box, unknown));
    }

    [Fact]
    public void PreCanceledConstructorLeavesEarlierSnapshotIndexesAndBoxesUntouched()
    {
        TestFixture fixture = CreateDecorativeImageFixture();
        using LayoutSnapshot prior = new(
            fixture.Roots,
            fixture.Context.SourceMap,
            width: 360,
            height: 640);
        SnapshotIndexState before = SnapshotIndexState.Capture(prior);
        Rect[] originalBounds = CaptureBounds(fixture);
        bool[] originalDirty = CaptureDirty(fixture);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => new LayoutSnapshot(
                fixture.Roots,
                fixture.Context.SourceMap,
                width: 480,
                height: 640));
        }

        before.AssertUnchanged(prior);
        Assert.Equal(originalBounds, CaptureBounds(fixture));
        Assert.Equal(originalDirty, CaptureDirty(fixture));
        Assert.True(prior.TryBeginPaint());
        prior.EndPaint();
    }

    private static TestFixture CreateDecorativeImageFixture()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = 101,
        };
        paragraph.Add(new TextRun("visible "));
        var link = new LinkRun("link", "https://example.test/link");
        paragraph.Add(link);
        var inlineDecorativeImage = new InlineImageRun(
            context,
            string.Empty,
            "https://example.test/inline-decorative.png");
        paragraph.Add(inlineDecorativeImage);

        var root = new StackBox { BlockIndex = 100 };
        root.Add(paragraph);
        var standaloneDecorativeImage = new ImageBox(
            context,
            "https://example.test/standalone-decorative.png",
            string.Empty)
        {
            BlockIndex = 102,
        };

        return new TestFixture(
            context,
            new BlockBox[] { root, standaloneDecorativeImage },
            root,
            paragraph,
            link,
            inlineDecorativeImage,
            standaloneDecorativeImage);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            black,
            black,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap("visible link"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static Rect[] CaptureBounds(TestFixture fixture) =>
    [
        fixture.Root.Bounds,
        fixture.Paragraph.Bounds,
        fixture.InlineDecorativeImage.Image.Bounds,
        fixture.StandaloneDecorativeImage.Bounds,
    ];

    private static bool[] CaptureDirty(TestFixture fixture) =>
    [
        fixture.Root.IsDirty,
        fixture.Paragraph.IsDirty,
        fixture.InlineDecorativeImage.Image.IsDirty,
        fixture.StandaloneDecorativeImage.IsDirty,
    ];

    private static T ReadField<T>(LayoutSnapshot snapshot, string name)
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Snapshot index field '{name}' was not found.");
        return Assert.IsAssignableFrom<T>(field.GetValue(snapshot));
    }

    private sealed record TestFixture(
        MarkdownLayoutContext Context,
        IReadOnlyList<BlockBox> Roots,
        StackBox Root,
        InlineContainerBox Paragraph,
        LinkRun Link,
        InlineImageRun InlineDecorativeImage,
        ImageBox StandaloneDecorativeImage);

    private sealed record SnapshotIndexState(
        Dictionary<BlockBox, int> BlockOrdinals,
        Dictionary<BlockBox, int> BlockOrdinalContents,
        IReadOnlyDictionary<int, int> TopLevelOrdinalByBlockIndex,
        Dictionary<int, int> TopLevelOrdinalContents,
        Dictionary<ImageBox, int> ImageTopLevelOrdinalByIdentity,
        Dictionary<ImageBox, int> ImageOrdinalContents,
        HashSet<IHorizontalOverflowBox> HorizontalOverflowBoxes,
        HashSet<IHorizontalOverflowBox> HorizontalOverflowContents,
        Dictionary<int, IHorizontalOverflowBox> HorizontalOverflowByBlockIndex,
        Dictionary<int, IHorizontalOverflowBox> HorizontalOverflowContentsByBlock,
        Array FocusablePlan,
        FocusPlanEntrySnapshot[] FocusablePlanContents,
        FocusableItem[]? FixedFocusableItems,
        FocusableItem[]? FixedFocusableContents)
    {
        internal static SnapshotIndexState Capture(LayoutSnapshot snapshot)
        {
            Dictionary<BlockBox, int> blockOrdinals = ReadField<Dictionary<BlockBox, int>>(snapshot, "_blockOrdinals");
            IReadOnlyDictionary<int, int> topLevel = ReadAssignableField<IReadOnlyDictionary<int, int>>(
                snapshot,
                "_topLevelOrdinalByBlockIndex");
            Dictionary<ImageBox, int> imageOrdinals = ReadField<Dictionary<ImageBox, int>>(
                snapshot,
                "_imageTopLevelOrdinalByIdentity");
            HashSet<IHorizontalOverflowBox> overflowBoxes = ReadField<HashSet<IHorizontalOverflowBox>>(
                snapshot,
                "_horizontalOverflowBoxes");
            Dictionary<int, IHorizontalOverflowBox> overflowByBlock = ReadField<Dictionary<int, IHorizontalOverflowBox>>(
                snapshot,
                "_horizontalOverflowByBlockIndex");
            Array focusPlan = ReadField<Array>(snapshot, "_focusablePlan");
            FocusableItem[]? fixedFocusable = ReadOptionalField<FocusableItem[]>(snapshot, "_fixedFocusableItems");

            return new SnapshotIndexState(
                blockOrdinals,
                new Dictionary<BlockBox, int>(blockOrdinals, ReferenceEqualityComparer.Instance),
                topLevel,
                new Dictionary<int, int>(topLevel),
                imageOrdinals,
                new Dictionary<ImageBox, int>(imageOrdinals, ReferenceEqualityComparer.Instance),
                overflowBoxes,
                new HashSet<IHorizontalOverflowBox>(overflowBoxes, ReferenceEqualityComparer.Instance),
                overflowByBlock,
                new Dictionary<int, IHorizontalOverflowBox>(overflowByBlock),
                focusPlan,
                CaptureFocusablePlan(snapshot),
                fixedFocusable,
                fixedFocusable is null ? null : (FocusableItem[])fixedFocusable.Clone());
        }

        internal void AssertUnchanged(LayoutSnapshot snapshot)
        {
            Assert.Same(BlockOrdinals, ReadField<Dictionary<BlockBox, int>>(snapshot, "_blockOrdinals"));
            AssertDictionaryContents(BlockOrdinalContents, BlockOrdinals);
            Assert.Same(TopLevelOrdinalByBlockIndex, ReadAssignableField<IReadOnlyDictionary<int, int>>(
                snapshot,
                "_topLevelOrdinalByBlockIndex"));
            AssertDictionaryContents(TopLevelOrdinalContents, TopLevelOrdinalByBlockIndex);
            Assert.Same(ImageTopLevelOrdinalByIdentity, ReadField<Dictionary<ImageBox, int>>(
                snapshot,
                "_imageTopLevelOrdinalByIdentity"));
            AssertDictionaryContents(ImageOrdinalContents, ImageTopLevelOrdinalByIdentity);
            Assert.Same(HorizontalOverflowBoxes, ReadField<HashSet<IHorizontalOverflowBox>>(
                snapshot,
                "_horizontalOverflowBoxes"));
            AssertReferenceSetContents(HorizontalOverflowContents, HorizontalOverflowBoxes);
            Assert.Same(HorizontalOverflowByBlockIndex, ReadField<Dictionary<int, IHorizontalOverflowBox>>(
                snapshot,
                "_horizontalOverflowByBlockIndex"));
            AssertDictionaryContents(HorizontalOverflowContentsByBlock, HorizontalOverflowByBlockIndex);
            Assert.Same(FocusablePlan, ReadField<Array>(snapshot, "_focusablePlan"));
            Assert.Equal(FocusablePlanContents, CaptureFocusablePlan(snapshot));

            FocusableItem[]? actualFixedFocusable = ReadOptionalField<FocusableItem[]>(snapshot, "_fixedFocusableItems");
            Assert.Same(FixedFocusableItems, actualFixedFocusable);
            Assert.Equal(FixedFocusableContents, actualFixedFocusable);
        }

        internal static FocusPlanEntrySnapshot[] CaptureFocusablePlan(LayoutSnapshot snapshot)
        {
            Array focusPlan = ReadField<Array>(snapshot, "_focusablePlan");
            var result = new FocusPlanEntrySnapshot[focusPlan.Length];
            for (int index = 0; index < focusPlan.Length; index++)
            {
                object entry = focusPlan.GetValue(index)!;
                Type entryType = entry.GetType();
                var item = Assert.IsType<FocusableItem>(
                    entryType.GetField("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                        .GetValue(entry));
                var overflow = (IHorizontalOverflowBox?)entryType
                    .GetField("HorizontalOverflow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(entry);
                result[index] = new FocusPlanEntrySnapshot(item, overflow);
            }
            return result;
        }

        private static void AssertDictionaryContents<TKey, TValue>(
            Dictionary<TKey, TValue> expected,
            IReadOnlyDictionary<TKey, TValue> actual)
            where TKey : notnull
        {
            Assert.Equal(expected.Count, actual.Count);
            foreach (KeyValuePair<TKey, TValue> entry in expected)
            {
                Assert.True(actual.TryGetValue(entry.Key, out TValue? value));
                Assert.Equal(entry.Value, value);
            }
        }

        private static void AssertReferenceSetContents<T>(HashSet<T> expected, HashSet<T> actual)
            where T : class
        {
            Assert.Equal(expected.Count, actual.Count);
            foreach (T item in expected)
                Assert.Contains(item, actual);
        }
    }

    private readonly record struct FocusPlanEntrySnapshot(
        FocusableItem Item,
        IHorizontalOverflowBox? Overflow);

    private static T? ReadOptionalField<T>(LayoutSnapshot snapshot, string name)
        where T : class
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Snapshot index field '{name}' was not found.");
        return field.GetValue(snapshot) as T;
    }

    private static T ReadAssignableField<T>(LayoutSnapshot snapshot, string name)
        where T : class
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Snapshot index field '{name}' was not found.");
        return Assert.IsAssignableFrom<T>(field.GetValue(snapshot));
    }

    private sealed class TestOverflowStackBox : StackBox, IHorizontalOverflowBox
    {
        public double HorizontalOffset => 0;
        public double HorizontalExtent => 200;
        public double HorizontalViewport => 100;
        public bool CanScrollHorizontally => true;
        public bool IsRightToLeft => false;
        public Rect HorizontalViewportBounds => default;
        public Rect HorizontalScrollTrackBounds => default;
        public Rect HorizontalScrollThumbBounds => default;
        public bool SetHorizontalOffset(double offset) => false;
        public bool ScrollHorizontal(double delta) => false;
    }

    private sealed class TestUnknownOverflowLeaf : BlockBox, IHorizontalOverflowBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 12);
            return 12;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport)
        {
        }

        public double HorizontalOffset => 0;
        public double HorizontalExtent => 200;
        public double HorizontalViewport => 100;
        public bool CanScrollHorizontally => true;
        public bool IsRightToLeft => false;
        public Rect HorizontalViewportBounds => default;
        public Rect HorizontalScrollTrackBounds => default;
        public Rect HorizontalScrollThumbBounds => default;
        public bool SetHorizontalOffset(double offset) => false;
        public bool ScrollHorizontal(double delta) => false;
    }
}
