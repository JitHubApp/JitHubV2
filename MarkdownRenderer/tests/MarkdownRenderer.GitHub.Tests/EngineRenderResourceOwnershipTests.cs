using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Image caches, budgets and native custody belong to an engine: engines
/// derived from an engine share its resources, and independently built
/// engines never share theirs.
/// </summary>
public sealed class EngineRenderResourceOwnershipTests
{
    [Fact]
    public void IndependentlyBuiltEnginesOwnSeparateResources()
    {
        using MarkdownEngine first = new MarkdownEngineBuilder().Build();
        using MarkdownEngine second = new MarkdownEngineBuilder().Build();

        Assert.NotSame(first.RenderResources, second.RenderResources);
        Assert.NotSame(first.RenderResources.Images, second.RenderResources.Images);
        Assert.NotSame(first.RenderResources.TextFormats, second.RenderResources.TextFormats);
        Assert.NotSame(first.RenderResources.PaintSessions, second.RenderResources.PaintSessions);
        Assert.NotSame(first.RenderResources.NativeRetirement, second.RenderResources.NativeRetirement);
        Assert.NotSame(MarkdownEngine.Default.RenderResources, first.RenderResources);
    }

    [Fact]
    public void DerivedEnginesShareTheirParentsResources()
    {
        using MarkdownEngine parent = new MarkdownEngineBuilder().Build();
        using MarkdownEngine rebuilt = parent.ToBuilder().Build();
        using MarkdownEngine reconfigured = new MarkdownEngineBuilder(parent)
            .WithParseCacheBudgetBytes(0)
            .Build();
        using MarkdownEngine grandchild = reconfigured.ToBuilder().Build();

        Assert.Same(parent.RenderResources, rebuilt.RenderResources);
        Assert.Same(parent.RenderResources, reconfigured.RenderResources);
        Assert.Same(parent.RenderResources, grandchild.RenderResources);
    }

    [Fact]
    public void TextFormatsAreSharedWithinOneOwnerOnly()
    {
        var owner = new MarkdownRenderResources();
        var other = new MarkdownRenderResources();
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 15 };

        using SharedCanvasTextFormatCache.Lease first = owner.TextFormats.Acquire(
            style, CanvasWordWrapping.Wrap, CanvasHorizontalAlignment.Left, FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease again = owner.TextFormats.Acquire(
            style, CanvasWordWrapping.Wrap, CanvasHorizontalAlignment.Left, FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease foreign = other.TextFormats.Acquire(
            style, CanvasWordWrapping.Wrap, CanvasHorizontalAlignment.Left, FlowDirection.LeftToRight);

        Assert.Same(first.Format, again.Format);
        Assert.NotSame(first.Format, foreign.Format);
        Assert.Equal(1, owner.TextFormats.Statistics.Count);
        Assert.Equal(1, other.TextFormats.Statistics.Count);
    }

    [Fact]
    public void DefaultAndProfileEnginesEachKeepOneOwner()
    {
        Assert.Same(MarkdownEngine.Default.RenderResources, MarkdownEngine.Default.RenderResources);
        Assert.Same(GfmMarkdownRenderer.SharedEngine.RenderResources, GfmMarkdownRenderer.SharedEngine.RenderResources);
        Assert.NotSame(MarkdownEngine.Default.RenderResources, GfmMarkdownRenderer.SharedEngine.RenderResources);
        Assert.NotSame(GitHubReadmeMarkdownRenderer.SharedEngine.RenderResources, GfmMarkdownRenderer.SharedEngine.RenderResources);
    }

    [Fact]
    public void ProductionOwnersCloseNativeSlotsWithoutAFaultPlan()
    {
        using MarkdownEngine built = new MarkdownEngineBuilder().Build();

        Assert.Null(built.RenderResources.NativeRetirement.Disposer);
        Assert.Null(MarkdownEngine.Default.RenderResources.NativeRetirement.Disposer);
        Assert.Null(GfmMarkdownRenderer.SharedEngine.RenderResources.NativeRetirement.Disposer);
        Assert.Null(new MarkdownRenderResources().NativeRetirement.Disposer);
    }

    [Fact]
    public void ContextsWithoutAnEngineAreIsolatedAndPlacementCandidatesKeepTheSourceOwner()
    {
        MarkdownLayoutContext first = CreateContext();
        MarkdownLayoutContext second = CreateContext();
        var owned = new MarkdownRenderResources();
        MarkdownLayoutContext engineContext = CreateContext(owned);

        Assert.NotSame(first.RenderResources, second.RenderResources);
        Assert.Same(first.RenderResources, first.RenderResources);
        Assert.Same(first.RenderResources.Images, first.ImageResources);
        Assert.Same(owned, engineContext.RenderResources);

        MarkdownLayoutContext candidate = engineContext.CreateRasterPlacementContext(
            CancellationToken.None,
            new object(),
            retirementSink: null,
            imageUnavailable: null);
        Assert.Same(owned, candidate.RenderResources);
        Assert.Same(owned.Images, candidate.ImageResources);
    }

    private static MarkdownLayoutContext CreateContext() =>
        new(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(),
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US");

    private static MarkdownLayoutContext CreateContext(MarkdownRenderResources resources) =>
        new(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(),
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = resources,
        };

    private static ThemeSnapshot CreateTheme()
    {
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }
}
