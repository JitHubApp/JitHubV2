using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class PrivateRasterPlacementStyleKeyCopyTests
{
    private const float Width = 320f;

    [Fact]
    public async Task ClonePreservesAliasesAndCountsEveryCopiedArrayEntryIncludingInheritedDuplicates()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        MarkdownLayoutContext target = fixture.CreateCandidateContext();
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            source = CreateStyledLeaf(
                fixture.SourceContext,
                containerContextKeyCount: 2,
                containerAliasCount: 2,
                runAliasCounts: new[] { 2, 1 },
                runModifierCounts: new[] { 1, 2 });

            Assert.True(source.TryGetPrivateRasterPlacementCounts(
                target,
                Width,
                out int runCount,
                out int imageCount,
                out long styleKeyCopyEntries,
                allowUnmeasuredTextOnly: true));
            Assert.Equal(2, runCount);
            Assert.Equal(0, imageCount);
            // context(2) + container aliases(2) + per-run aliases/modifiers
            // + the two explicit effective overrides (4 and 3).
            Assert.Equal(17, styleKeyCopyEntries);

            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                target,
                Width,
                out clone,
                out _,
                allowUnmeasuredTextOnly: true));
            InlineContainerBox copied = Assert.IsType<InlineContainerBox>(clone);
            Assert.True(copied.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long copiedEntries));
            Assert.Equal(styleKeyCopyEntries, copiedEntries);

            for (int index = 0; index < source.Runs.Count; index++)
            {
                Assert.Equal(source.Runs[index].StyleAliases, copied.Runs[index].StyleAliases);
                Assert.NotSame(source.Runs[index].StyleAliases, copied.Runs[index].StyleAliases);
                Assert.Equal(source.Runs[index].StyleModifierKeys, copied.Runs[index].StyleModifierKeys);
                Assert.NotSame(source.Runs[index].StyleModifierKeys, copied.Runs[index].StyleModifierKeys);
            }

            Assert.Equal(GetKeyList(source, "_styleContextKeys"), GetKeyList(copied, "_styleContextKeys"));
            Assert.NotSame(GetKeyList(source, "_styleContextKeys"), GetKeyList(copied, "_styleContextKeys"));
            Assert.Equal(GetKeyList(source, "_styleAliasKeys"), GetKeyList(copied, "_styleAliasKeys"));
            Assert.NotSame(GetKeyList(source, "_styleAliasKeys"), GetKeyList(copied, "_styleAliasKeys"));
            IReadOnlyList<IReadOnlyList<string>> sourceEffectiveAliases = GetEffectiveAliases(source);
            IReadOnlyList<IReadOnlyList<string>> copiedEffectiveAliases = GetEffectiveAliases(copied);
            Assert.Equal(sourceEffectiveAliases.Count, copiedEffectiveAliases.Count);
            for (int index = 0; index < sourceEffectiveAliases.Count; index++)
            {
                Assert.Equal(sourceEffectiveAliases[index], copiedEffectiveAliases[index]);
                Assert.NotSame(sourceEffectiveAliases[index], copiedEffectiveAliases[index]);
            }
        }
        finally
        {
            try
            {
                if (clone is not null)
                    target.DiscardConstructionBlock(clone);
            }
            finally
            {
                try
                {
                    if (source is not null)
                        fixture.SourceContext.DiscardConstructionBlock(source);
                }
                finally
                {
                    await Task.WhenAll(
                        target.DrainOwnedRetirementsAsync(),
                        fixture.SourceContext.DrainOwnedRetirementsAsync())
                        .WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    [Fact]
    public async Task AggregatePreflightAndCloneEntryRejectOverBudgetWithoutTrimmingAliases()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        MarkdownLayoutContext target = fixture.CreateCandidateContext();
        InlineContainerBox? first = null;
        InlineContainerBox? second = null;
        StackBox? composite = null;
        try
        {
            const int containerAliasCount = InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries / 2 + 1;
            first = CreateStyledLeaf(
                fixture.SourceContext, containerContextKeyCount: 0,
                containerAliasCount: containerAliasCount,
                runAliasCounts: new[] { 0 }, runModifierCounts: new[] { 0 });
            second = CreateStyledLeaf(
                fixture.SourceContext, containerContextKeyCount: 0,
                containerAliasCount: containerAliasCount,
                runAliasCounts: new[] { 0 }, runModifierCounts: new[] { 0 });

            Assert.True(first.TryGetPrivateRasterPlacementStyleKeyCopyEntries(out long perRootEntries));
            Assert.Equal(containerAliasCount, perRootEntries);
            Assert.True(perRootEntries <= InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries);

            var capture = new PrivateRasterPlacementTree.CaptureState(allowUnmeasuredTextOnly: true);
            Assert.True(capture.TryAddRoot(
                first, fixture.SourceContext, target, Width, out PrivateRasterPlacementTree.RootCounts firstCounts));
            Assert.Equal(perRootEntries, firstCounts.StyleKeyCopyEntries);
            Assert.False(capture.TryAddRoot(
                second, fixture.SourceContext, target, Width, out _));

            Assert.False(MarkdownRenderer.Performance.MarkdownVectorSceneResidencyPolicy
                .CanOmitForPrivatePlacement(new BlockBox[] { first, second }));

            composite = new StackBox { BlockIndex = 0 };
            composite.Add(first);
            composite.Add(second);
            first = null;
            second = null;

            Assert.False(PrivateRasterPlacementTree.TryCreateClone(
                composite,
                fixture.SourceContext,
                target,
                Width,
                out BlockBox? clone,
                out RasterPlacementSourceFence[]? fences,
                allowUnmeasuredTextOnly: true));
            Assert.Null(clone);
            Assert.Null(fences);
        }
        finally
        {
            try
            {
                if (composite is not null)
                    fixture.SourceContext.DiscardConstructionBlock(composite);
                if (first is not null)
                    fixture.SourceContext.DiscardConstructionBlock(first);
                if (second is not null)
                    fixture.SourceContext.DiscardConstructionBlock(second);
            }
            finally
            {
                await Task.WhenAll(
                    target.DrainOwnedRetirementsAsync(),
                    fixture.SourceContext.DrainOwnedRetirementsAsync())
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static InlineContainerBox CreateStyledLeaf(
        MarkdownLayoutContext context,
        int containerContextKeyCount,
        int containerAliasCount,
        int[] runAliasCounts,
        int[] runModifierCounts)
    {
        if (runAliasCounts.Length != runModifierCounts.Length)
            throw new ArgumentException("Run style-key fixture lengths must match.");

        var contextScopes = new MarkdownLayoutContext.StyleScope[containerContextKeyCount];
        try
        {
            for (int index = 0; index < contextScopes.Length; index++)
                contextScopes[index] = context.PushStyleContext(MarkdownElementKeys.Strong);

            using MarkdownLayoutContext.StyleScope aliasScope = context.PushStyleAliases(CreateKeys(containerAliasCount));
            var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
            {
                BlockIndex = context.NextBlockIndex(),
            };
            for (int index = 0; index < runAliasCounts.Length; index++)
            {
                inline.Add(new TextRun($"run-{index}")
                {
                    StyleAliases = CreateKeys(runAliasCounts[index]),
                    StyleModifierKeys = CreateKeys(runModifierCounts[index]),
                });
            }
            return inline;
        }
        finally
        {
            for (int index = contextScopes.Length - 1; index >= 0; index--)
                contextScopes[index].Dispose();
        }
    }

    private static string[] CreateKeys(int count)
    {
        var keys = new string[count];
        Array.Fill(keys, "private-style-key");
        return keys;
    }

    private static IReadOnlyList<string> GetKeyList(InlineContainerBox box, string fieldName)
    {
        FieldInfo field = typeof(InlineContainerBox).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(field.GetValue(box));
    }

    private static IReadOnlyList<IReadOnlyList<string>> GetEffectiveAliases(InlineContainerBox box)
    {
        MethodInfo getter = typeof(InlineContainerBox).GetMethod(
            "GetEffectiveRunAliases",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = new IReadOnlyList<string>[box.Runs.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = Assert.IsType<string[]>(getter.Invoke(box, new object[] { index }));
        }
        return result;
    }
}
