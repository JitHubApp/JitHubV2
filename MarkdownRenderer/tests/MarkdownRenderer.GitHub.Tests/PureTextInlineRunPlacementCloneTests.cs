using System;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PureTextInlineRunPlacementCloneTests
{
    [Fact]
    public void CodeInlinePlacementClonePreservesRuntimeAndSourceStyleMetadata()
    {
        var source = Stamp(new CodeInlineRun("`build.ps1`"));
        InlineRun clone = source.CloneForPrivateRasterPlacement();

        AssertRunMetadataAndPrivateStyleCopies(source, clone);
        Assert.Equal(MarkdownElementKeys.CodeInline, clone.ElementKey);
        Assert.Equal("`build.ps1`", clone.Text);
        Assert.Equal("`build.ps1`", clone.LayoutText);
        Assert.Equal("`build.ps1`", clone.AccessibleText);
    }

    [Fact]
    public void BuiltInStatelessTextRunsCloneByValueWithoutSharingStyleLists()
    {
        (InlineRun Source, Func<InlineRun, InlineRun> Clone)[] cases =
        [
            (new TextRun("text"), run => ((TextRun)run).CloneForPrivateRasterPlacement()),
            (new StrongRun("strong"), run => ((StrongRun)run).CloneForPrivateRasterPlacement()),
            (new EmphasisRun("emphasis"), run => ((EmphasisRun)run).CloneForPrivateRasterPlacement()),
            (new StrikethroughRun("strike"), run => ((StrikethroughRun)run).CloneForPrivateRasterPlacement()),
            (new SubscriptRun("sub"), run => ((SubscriptRun)run).CloneForPrivateRasterPlacement()),
            (new SuperscriptRun("super"), run => ((SuperscriptRun)run).CloneForPrivateRasterPlacement()),
            (new InsertedRun("inserted"), run => ((InsertedRun)run).CloneForPrivateRasterPlacement()),
            (new MarkedRun("marked"), run => ((MarkedRun)run).CloneForPrivateRasterPlacement()),
        ];

        foreach ((InlineRun uninitialized, Func<InlineRun, InlineRun> cloneRun) in cases)
        {
            InlineRun source = Stamp(uninitialized);
            InlineRun clone = cloneRun(source);
            AssertRunMetadataAndPrivateStyleCopies(source, clone);
            Assert.Equal(source.Text, clone.Text);
            Assert.Equal(source.LayoutText, clone.LayoutText);
            Assert.Equal(source.AccessibleText, clone.AccessibleText);
        }
    }

    [Fact]
    public void AbbreviationPlacementClonePreservesExpansionAndAccessibleText()
    {
        var source = Stamp(new AbbreviationRun("CPU", "central processing unit"));
        AbbreviationRun clone = source.CloneForPrivateRasterPlacement();

        AssertRunMetadataAndPrivateStyleCopies(source, clone);
        Assert.Equal("central processing unit", clone.Expansion);
        Assert.Equal("CPU", clone.Text);
        Assert.Equal("CPU", clone.LayoutText);
        Assert.Equal("CPU (central processing unit)", clone.AccessibleText);
    }

    [Fact]
    public void LineBreakPlacementClonePreservesSemanticAndSingleLineLayoutState()
    {
        var hardBreak = Stamp(new LineBreakRun(isHard: true)
        {
            UseSingleLineLayout = true,
        });
        LineBreakRun hardClone = hardBreak.CloneForPrivateRasterPlacement();

        AssertRunMetadataAndPrivateStyleCopies(hardBreak, hardClone);
        Assert.True(hardClone.UseSingleLineLayout);
        Assert.Equal("\n", hardClone.Text);
        Assert.Equal(" ", hardClone.LayoutText);
        Assert.Equal("\n", hardClone.AccessibleText);
        Assert.Equal(1, hardClone.RenderedLength);

        var softBreak = Stamp(new LineBreakRun(isHard: false));
        LineBreakRun softClone = softBreak.CloneForPrivateRasterPlacement();
        AssertRunMetadataAndPrivateStyleCopies(softBreak, softClone);
        Assert.False(softClone.UseSingleLineLayout);
        Assert.Equal(" ", softClone.Text);
        Assert.Equal(" ", softClone.LayoutText);
    }

    private static T Stamp<T>(T run) where T : InlineRun
    {
        run.InlineIndex = 7;
        run.SourceSpan = new SourceSpan(31, run.Text.Length);
        run.SetStyleAliases(new[] { "language-csharp", "dense" });
        run.StyleModifierKeys = new[] { MarkdownElementKeys.Strong, MarkdownElementKeys.Emphasis };
        run.SemanticHeadingKey = MarkdownElementKeys.Heading3;
        return run;
    }

    private static void AssertRunMetadataAndPrivateStyleCopies(InlineRun source, InlineRun clone)
    {
        Assert.NotSame(source, clone);
        Assert.Equal(source.GetType(), clone.GetType());
        Assert.Equal(source.InlineIndex, clone.InlineIndex);
        Assert.Equal(source.RenderedLength, clone.RenderedLength);
        Assert.Equal(source.SourceSpan, clone.SourceSpan);
        Assert.Equal(source.ElementKey, clone.ElementKey);
        Assert.Equal(source.SemanticHeadingKey, clone.SemanticHeadingKey);

        string[] sourceAliases = Assert.IsType<string[]>(source.StyleAliases);
        string[] sourceModifiers = Assert.IsType<string[]>(source.StyleModifierKeys);
        string[] cloneAliases = Assert.IsType<string[]>(clone.StyleAliases);
        string[] cloneModifiers = Assert.IsType<string[]>(clone.StyleModifierKeys);
        Assert.NotSame(sourceAliases, cloneAliases);
        Assert.NotSame(sourceModifiers, cloneModifiers);
        Assert.Equal(new[] { "language-csharp", "dense" }, cloneAliases);
        Assert.Equal(new[] { MarkdownElementKeys.Strong, MarkdownElementKeys.Emphasis }, cloneModifiers);

        sourceAliases[0] = "mutated-source";
        sourceModifiers[0] = "mutated-source";
        Assert.Equal("language-csharp", cloneAliases[0]);
        Assert.Equal(MarkdownElementKeys.Strong, cloneModifiers[0]);
    }
}
