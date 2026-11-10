using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBoxActivationResetTests : IDisposable
{
    // Offscreen release lets a box suspend and resume its resources.
    private readonly MarkdownPerformanceSession _session = new(
        MarkdownPerformanceOptions.Progressive with
        {
            PrefetchDocumentImages = false,
            ReleaseOffscreenImageResources = true,
        });

    public void Dispose() => _session.Dispose();

    [Fact]
    public void CurrentUnstartedActivationClearsItsOwnLoadStartedState()
    {
        MarkdownSvgRenderDemand demand = OverscanDemand();
        ImageBox box = StartedBox(demand, out int loadGeneration);

        box.Activation.ResetLoadStartedAfterUnstartedActivation(demand, loadGeneration, CancellationToken.None);

        Assert.False(box.LoadState.IsStarted);
    }

    [Fact]
    public void StaleDemandOrGenerationCannotClearCurrentLoadStartedState()
    {
        MarkdownSvgRenderDemand current = OverscanDemand();
        ImageBox box = StartedBox(current, out int loadGeneration);

        box.Activation.ResetLoadStartedAfterUnstartedActivation(
            OverscanDemand(), loadGeneration, CancellationToken.None);
        Assert.True(box.LoadState.IsStarted);

        box.Activation.ResetLoadStartedAfterUnstartedActivation(
            current, loadGeneration - 1, CancellationToken.None);
        Assert.True(box.LoadState.IsStarted);
    }

    [Fact]
    public void CanceledQueuedActivationCannotClearLoadStartedState()
    {
        MarkdownSvgRenderDemand demand = OverscanDemand();
        ImageBox box = StartedBox(demand, out int loadGeneration);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        box.Activation.ResetLoadStartedAfterUnstartedActivation(demand, loadGeneration, cancellation.Token);

        Assert.True(box.LoadState.IsStarted);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DisposedOrSuspendedBoxDoesNotResetLoadStarted(
        bool disposed,
        bool suspended)
    {
        MarkdownSvgRenderDemand demand = OverscanDemand();
        ImageBox box = StartedBox(demand, out int loadGeneration);
        if (suspended)
            box.SuspendResources();
        if (disposed)
            box.Dispose();
        Assert.Equal(disposed, box.Lifetime.IsDisposed);
        Assert.Equal(suspended, box.Lifetime.IsSuspended);

        box.Activation.ResetLoadStartedAfterUnstartedActivation(demand, loadGeneration, CancellationToken.None);

        Assert.True(box.LoadState.IsStarted);
    }

    [Fact]
    public void OldCanceledActivationCannotResetResumedLoadWithSameDemandAndGeneration()
    {
        MarkdownSvgRenderDemand demand = OverscanDemand();
        ImageBox box = StartedBox(demand, out int loadGeneration);
        using var oldResidencyCancellation = new CancellationTokenSource();
        oldResidencyCancellation.Cancel();

        // Model a resume that deliberately reuses the load identity while a
        // late callback from the canceled pre-suspend activation is delivered.
        box.SuspendResources();
        Assert.True(box.TryResumeResources());
        Assert.False(box.Lifetime.IsSuspended);
        box.LoadState.IsStarted = true;
        Assert.Same(demand, box.LoadState.Demand);
        Assert.Equal(loadGeneration, box.LoadState.DemandGeneration);
        box.Activation.ResetLoadStartedAfterUnstartedActivation(
            demand, loadGeneration, oldResidencyCancellation.Token);

        Assert.True(box.LoadState.IsStarted);
    }

    // A real box whose current load demand is demand and whose load has
    // started, without running any load.
    private ImageBox StartedBox(MarkdownSvgRenderDemand demand, out int loadGeneration)
    {
        var box = new ImageBox(
            CreateContext(),
            new ImageResourceScope(),
            $"https://example.test/activation-{Guid.NewGuid():N}.svg",
            "Activation");
        Assert.False(box.LoadState.IsStarted);
        loadGeneration = box.LoadState.BeginDemand(demand);
        box.LoadState.IsStarted = true;
        return box;
    }

    private static MarkdownSvgRenderDemand OverscanDemand() =>
        new(MarkdownSvgRenderPriority.Overscan);

    private MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.ImageCaption,
        ];
        var styles = keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 64)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            PerformanceSession = _session,
            PerformanceDocumentOwner = new object(),
        };
    }
}
