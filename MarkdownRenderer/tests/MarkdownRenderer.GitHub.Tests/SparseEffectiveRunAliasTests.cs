using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SparseEffectiveRunAliasTests
{
    [Fact]
    public async Task EveryRunOverrideRetainsItsOrdinalAndContainerThenRunOrder()
    {
        await using var contexts = new ContextPair("one two three");
        InlineContainerBox? source = null;
        try
        {
            using (contexts.Source.PushStyleAliases(new[] { "outer-a", "outer-b" }))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("one") { StyleAliases = new[] { "first" } },
                    new TextRun(" two") { StyleAliases = new[] { "second-a", "second-b" } },
                    new TextRun(" three") { StyleAliases = new[] { "third" } });
            }

            IReadOnlyList<string[]?> slots = GetOverrideSlots(source)!;
            Assert.Equal(3, slots.Count);
            Assert.Equal(new[] { "outer-a", "outer-b", "first" }, slots[0]);
            Assert.Equal(new[] { "outer-a", "outer-b", "second-a", "second-b" }, slots[1]);
            Assert.Equal(new[] { "outer-a", "outer-b", "third" }, slots[2]);
            for (int index = 0; index < slots.Count; index++)
                Assert.Same(slots[index], GetEffectiveAliases(source, index));
            Assert.True(source.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long entries));
            Assert.Equal(16, entries); // container(2) + run arrays(4) + overrides(10)
        }
        finally
        {
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task RunsWithoutOverridesUseOneStableContainerArrayAndCloneItsOwnFallback()
    {
        await using var contexts = new ContextPair("first second third");
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            string[] scopedAliases = ["container-a", "container-b"];
            using (contexts.Source.PushStyleAliases(scopedAliases))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("first"),
                    new TextRun(" second"),
                    new TextRun(" third"));
            }

            Assert.Null(GetOverrideSlots(source));
            string[] sourceContainerAliases = GetContainerAliases(source);
            Assert.Equal(scopedAliases, sourceContainerAliases);
            Assert.Same(sourceContainerAliases, GetEffectiveAliases(source, 0));
            Assert.Same(sourceContainerAliases, GetEffectiveAliases(source, 1));
            Assert.Same(sourceContainerAliases, GetEffectiveAliases(source, 2));
            Assert.True(source.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long sourceEntries));
            Assert.Equal(scopedAliases.Length, sourceEntries);

            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                contexts.Candidate,
                320f,
                out clone,
                out _,
                allowUnmeasuredTextOnly: true));

            Assert.Null(GetOverrideSlots(clone!));
            string[] cloneContainerAliases = GetContainerAliases(clone!);
            Assert.Equal(scopedAliases, cloneContainerAliases);
            Assert.NotSame(sourceContainerAliases, cloneContainerAliases);
            for (int index = 0; index < source.Runs.Count; index++)
            {
                string[] sourceAliases = GetEffectiveAliases(source, index);
                string[] cloneAliases = GetEffectiveAliases(clone!, index);
                Assert.Equal(sourceAliases, cloneAliases);
                Assert.NotSame(sourceAliases, cloneAliases);
                Assert.Same(cloneContainerAliases, cloneAliases);
            }
            Assert.True(clone!.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long cloneEntries));
            Assert.Equal(scopedAliases.Length, cloneEntries);
        }
        finally
        {
            await RetireAsync(contexts.Candidate, clone);
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task LateRunOverridePreservesAddTimeConcatenationAndNullOrdinalsAcrossClone()
    {
        await using var contexts = new ContextPair("first second third fourth");
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            string[] containerAliases = ["scope"];
            string[] mutableRunAliases = ["captured"];
            var overriddenRun = new TextRun(" third") { StyleAliases = mutableRunAliases };
            using (contexts.Source.PushStyleAliases(containerAliases))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("first"),
                    new TextRun(" second"),
                    overriddenRun,
                    new TextRun(" fourth"));
            }

            mutableRunAliases[0] = "mutated-after-add";
            IReadOnlyList<string[]?> slots = GetOverrideSlots(source)!;
            Assert.Equal(4, slots.Count);
            Assert.Null(slots[0]);
            Assert.Null(slots[1]);
            Assert.Equal(new[] { "scope", "captured" }, slots[2]);
            Assert.Null(slots[3]);
            Assert.Equal(new[] { "scope", "captured" }, GetEffectiveAliases(source, 2));
            Assert.NotSame(mutableRunAliases, GetEffectiveAliases(source, 2));
            Assert.True(source.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long sourceEntries));
            Assert.Equal(4, sourceEntries); // scope + run alias + combined override

            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                contexts.Candidate,
                320f,
                out clone,
                out _,
                allowUnmeasuredTextOnly: true));

            Assert.NotNull(clone);
            IReadOnlyList<string[]?> copiedSlots = GetOverrideSlots(clone)!;
            Assert.Equal(4, copiedSlots.Count);
            Assert.Null(copiedSlots[0]);
            Assert.Null(copiedSlots[1]);
            Assert.Equal(new[] { "scope", "captured" }, copiedSlots[2]);
            Assert.Null(copiedSlots[3]);
            Assert.NotSame(slots[2], copiedSlots[2]);
            Assert.Equal(new[] { "mutated-after-add" }, clone.Runs[2].StyleAliases);
            Assert.Equal(new[] { "scope", "captured" }, GetEffectiveAliases(clone, 2));
            Assert.True(clone.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long cloneEntries));
            Assert.Equal(sourceEntries, cloneEntries);
        }
        finally
        {
            await RetireAsync(contexts.Candidate, clone);
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task EmptyContainerKeepsMutableArrayIdentityAtAddButCloneCopiesIt()
    {
        await using var contexts = new ContextPair("mutable");
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            string[] aliases = ["before"];
            source = CreateBox(contexts.Source, new TextRun("mutable") { StyleAliases = aliases });

            IReadOnlyList<string[]?> slots = GetOverrideSlots(source)!;
            Assert.Single(slots);
            Assert.Same(aliases, slots[0]);
            Assert.Same(aliases, GetEffectiveAliases(source, 0));
            aliases[0] = "after";
            Assert.Equal(new[] { "after" }, GetEffectiveAliases(source, 0));
            Assert.True(source.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long sourceEntries));
            Assert.Equal(2, sourceEntries); // run aliases and its separately retained effective override

            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                contexts.Candidate,
                320f,
                out clone,
                out _,
                allowUnmeasuredTextOnly: true));

            Assert.Equal(new[] { "after" }, clone!.Runs[0].StyleAliases);
            Assert.Equal(new[] { "after" }, GetEffectiveAliases(clone, 0));
            Assert.NotSame(aliases, clone.Runs[0].StyleAliases);
            Assert.NotSame(aliases, GetEffectiveAliases(clone, 0));
            Assert.NotSame(GetEffectiveAliases(source, 0), GetEffectiveAliases(clone, 0));
        }
        finally
        {
            await RetireAsync(contexts.Candidate, clone);
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task DetachImagesDiscardsOverridesAndRebuildsFromCurrentRemainingRunAliases()
    {
        await using var contexts = new ContextPair("before image after image");
        InlineContainerBox? source = null;
        InlineContainerBox? destination = null;
        InlineImageRun? detachedImage = null;
        bool imageAdopted = false;
        try
        {
            string[] containerAliases = ["scope"];
            var afterRun = new TextRun(" after") { StyleAliases = new[] { "old-after" } };
            using (contexts.Source.PushStyleAliases(containerAliases))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("before"),
                    new InlineImageRun(contexts.Source, "image", "https://example.test/image.png")
                    {
                        StyleAliases = new[] { "image" },
                    },
                    afterRun,
                    new TextRun(" image"));

                Assert.Equal(new[] { "scope", "old-after" }, GetEffectiveAliases(source, 2));
                afterRun.StyleAliases = new[] { "updated-after" };

                InlineImageRun[] detached = source.DetachInlineImagesForTransfer();
                detachedImage = Assert.Single(detached);
                Assert.Equal(3, source.Runs.Count);
                Assert.Equal(0, source.Runs[0].InlineIndex);
                Assert.Equal(1, source.Runs[1].InlineIndex);
                Assert.Equal(2, source.Runs[2].InlineIndex);
                Assert.Same(GetContainerAliases(source), GetEffectiveAliases(source, 0));
                Assert.Equal(new[] { "scope", "updated-after" }, GetEffectiveAliases(source, 1));
                Assert.Equal(new[] { "scope" }, GetEffectiveAliases(source, 2));

                destination = new InlineContainerBox(contexts.Source, MarkdownElementKeys.Body)
                {
                    BlockIndex = contexts.Source.NextBlockIndex(),
                };
                destination.Add(detachedImage);
                imageAdopted = true;
                Assert.Equal(0, detachedImage.InlineIndex);
                Assert.Equal(new[] { "scope", "image" }, GetEffectiveAliases(destination, 0));
            }
        }
        finally
        {
            try
            {
                if (destination is not null)
                    contexts.Source.DiscardConstructionBlock(destination);
            }
            finally
            {
                try
                {
                    if (detachedImage is not null && !imageAdopted)
                        contexts.Source.DiscardConstructionBlock(detachedImage.Image);
                }
                finally
                {
                    await RetireAsync(contexts.Source, source);
                }
            }
        }
    }

    [Fact]
    public async Task DetachImagesWithInheritedAliasesKeepsTheSparseOverrideStoreAbsent()
    {
        await using var contexts = new ContextPair("left image right");
        InlineContainerBox? source = null;
        InlineImageRun? detachedImage = null;
        try
        {
            string[] containerAliases = ["container"];
            using (contexts.Source.PushStyleAliases(containerAliases))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("left"),
                    new InlineImageRun(contexts.Source, "image", "https://example.test/image.png"),
                    new TextRun(" right"));
            }

            InlineImageRun[] detached = source.DetachInlineImagesForTransfer();
            detachedImage = Assert.Single(detached);

            Assert.Equal(2, source.Runs.Count);
            Assert.Null(GetOverrideSlots(source));
            Assert.Same(GetContainerAliases(source), GetEffectiveAliases(source, 0));
            Assert.Same(GetContainerAliases(source), GetEffectiveAliases(source, 1));
        }
        finally
        {
            if (detachedImage is not null)
                contexts.Source.DiscardConstructionBlock(detachedImage.Image);
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task DetachImagesPreservesFirstAndLastRetainedOverridesAndNullOrdinals()
    {
        await using var contexts = new ContextPair("first image middle image last");
        InlineContainerBox? source = null;
        InlineImageRun[] detachedImages = Array.Empty<InlineImageRun>();
        try
        {
            using (contexts.Source.PushStyleAliases(new[] { "scope" }))
            {
                source = CreateBox(contexts.Source,
                    new TextRun("first") { StyleAliases = new[] { "first-run" } },
                    new InlineImageRun(contexts.Source, "image", "https://example.test/first.png"),
                    new TextRun(" middle"),
                    new InlineImageRun(contexts.Source, "image", "https://example.test/last.png"),
                    new TextRun(" last") { StyleAliases = new[] { "last-run" } });
            }

            detachedImages = source.DetachInlineImagesForTransfer();

            Assert.Equal(2, detachedImages.Length);
            Assert.Equal(3, source.Runs.Count);
            IReadOnlyList<string[]?> slots = GetOverrideSlots(source)!;
            Assert.Equal(3, slots.Count);
            Assert.Equal(new[] { "scope", "first-run" }, slots[0]);
            Assert.Null(slots[1]);
            Assert.Equal(new[] { "scope", "last-run" }, slots[2]);
            Assert.Same(slots[0], GetEffectiveAliases(source, 0));
            Assert.Same(GetContainerAliases(source), GetEffectiveAliases(source, 1));
            Assert.Same(slots[2], GetEffectiveAliases(source, 2));
        }
        finally
        {
            try
            {
                foreach (InlineImageRun image in detachedImages)
                    contexts.Source.DiscardConstructionBlock(image.Image);
            }
            finally
            {
                await RetireAsync(contexts.Source, source);
            }
        }
    }

    [Fact]
    public async Task DetachingAllImageRunsLeavesAnEmptyBoxWithoutAnOverrideStore()
    {
        await using var contexts = new ContextPair("image");
        InlineContainerBox? source = null;
        InlineImageRun? detachedImage = null;
        try
        {
            source = CreateBox(contexts.Source,
                new InlineImageRun(contexts.Source, "image", "https://example.test/image.png"));

            detachedImage = Assert.Single(source.DetachInlineImagesForTransfer());

            Assert.Empty(source.Runs);
            Assert.Null(GetOverrideSlots(source));
        }
        finally
        {
            if (detachedImage is not null)
                contexts.Source.DiscardConstructionBlock(detachedImage.Image);
            await RetireAsync(contexts.Source, source);
        }
    }

    [Fact]
    public async Task NormalAliasAppendStillRejectsNonLastAndOutOfRangeOrdinals()
    {
        await using var contexts = new ContextPair("first second");
        InlineContainerBox? source = null;
        try
        {
            source = CreateBox(contexts.Source,
                new TextRun("first") { StyleAliases = new[] { "captured" } },
                new TextRun(" second"));

            IReadOnlyList<string[]?> beforeSlots = GetOverrideSlots(source)!;
            var before = new string[]?[beforeSlots.Count];
            for (int index = 0; index < before.Length; index++)
                before[index] = beforeSlots[index];
            MethodInfo append = typeof(InlineContainerBox).GetMethod(
                "AppendEffectiveRunAliasOverride",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            TargetInvocationException nonLast = Assert.Throws<TargetInvocationException>(
                () => append.Invoke(source, new object?[] { 0, new[] { "invalid" } }));
            Assert.IsType<InvalidOperationException>(nonLast.InnerException);
            TargetInvocationException outOfRange = Assert.Throws<TargetInvocationException>(
                () => append.Invoke(source, new object?[] { source.Runs.Count, new[] { "invalid" } }));
            Assert.IsType<InvalidOperationException>(outOfRange.InnerException);

            IReadOnlyList<string[]?> after = GetOverrideSlots(source)!;
            Assert.Equal(before.Length, after.Count);
            for (int index = 0; index < before.Length; index++)
                Assert.Same(before[index], after[index]);
            Assert.Equal(new[] { "captured" }, GetEffectiveAliases(source, 0));
            Assert.Empty(GetEffectiveAliases(source, 1));
        }
        finally
        {
            await RetireAsync(contexts.Source, source);
        }
    }

    private static InlineContainerBox CreateBox(
        MarkdownLayoutContext context,
        params InlineRun[] runs)
    {
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        int sourceOffset = 0;
        foreach (InlineRun run in runs)
        {
            run.SourceSpan = new global::MarkdownRenderer.SourceSpan(sourceOffset, run.TextLength);
            box.Add(run);
            sourceOffset += run.TextLength;
        }
        return box;
    }

    private static object? GetOverrideStore(InlineContainerBox box)
        => GetOverrideField().GetValue(box);

    private static IReadOnlyList<string[]?>? GetOverrideSlots(InlineContainerBox box)
        => GetOverrideStore(box) is { } value
            ? Assert.IsAssignableFrom<IReadOnlyList<string[]?>>(value)
            : null;

    private static string[] GetContainerAliases(InlineContainerBox box)
        => Assert.IsType<string[]>(GetContainerAliasField().GetValue(box));

    private static string[] GetEffectiveAliases(InlineContainerBox box, int index)
        => Assert.IsType<string[]>(GetEffectiveAliasGetter().Invoke(box, new object[] { index }));

    private static FieldInfo GetOverrideField()
        => typeof(InlineContainerBox).GetField("_effectiveRunAliasOverrides", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo GetContainerAliasField()
        => typeof(InlineContainerBox).GetField("_styleAliasKeys", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo GetEffectiveAliasGetter()
        => typeof(InlineContainerBox).GetMethod("GetEffectiveRunAliases", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static async Task RetireAsync(MarkdownLayoutContext context, BlockBox? box)
    {
        if (box is not null)
            context.DiscardConstructionBlock(box);
        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class ContextPair : IAsyncDisposable
    {
        private readonly ThemeSnapshot _theme;
        private readonly MarkdownSourceMap _sourceMap;
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
        private readonly IReadOnlyDictionary<string, bool> _disclosureStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        internal ContextPair(string sourceText)
        {
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            Color transparent = Color.FromArgb(0, 0, 0, 0);
            _theme = new ThemeSnapshot(
                new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = body },
                new Dictionary<string, ElementStyleOverride>(),
                transparent,
                transparent,
                transparent,
                transparent,
                isDark: false,
                isHighContrast: false,
                textScaleFactor: 1f);
            _sourceMap = new MarkdownSourceMap(sourceText);
            Source = CreateContext();
            Candidate = CreateContext();
        }

        internal MarkdownLayoutContext Source { get; }
        internal MarkdownLayoutContext Candidate { get; }

        private MarkdownLayoutContext CreateContext()
            => new(
                _device,
                _theme,
                _sourceMap,
                _registry,
                FlowDirection.LeftToRight)
            {
                DisclosureStates = _disclosureStates,
            };

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(
                Source.DrainOwnedRetirementsAsync(),
                Candidate.DrainOwnedRetirementsAsync())
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
