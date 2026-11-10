using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Markdig.Syntax;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A provider request can outlive the snapshot it was made for. While the
/// provider is still working, the highlight task, its continuations and the
/// UI publication it later queues carry only a weak snapshot identity and the
/// copied code text, so the retired snapshot and its code block can be
/// collected; the late result then asks for a fresh pass instead of
/// publishing to a snapshot that is gone.
/// </summary>
public sealed class CodeBlockHighlightSnapshotRetentionTests
{
    private const string Code = "var x = 1;";

    [Fact]
    public async Task PendingProviderRequestRetainsNeitherTheSnapshotNorItsCodeBlock()
    {
        var harness = new HighlightHarness();
        await harness.Highlighting.Restart();

        SnapshotProbe probe = ScheduleOnUnreferencedSnapshot(harness);

        Assert.Equal(1, harness.Provider.Requests);
        Assert.Equal(Code, harness.Provider.LastCode);
        Assert.False(harness.Provider.Completion.Task.IsCompleted);
        RetentionCollector.CollectUntilDead(probe.Snapshot, probe.Block, probe.Context);
        Assert.False(probe.Snapshot.IsAlive);
        Assert.False(probe.Block.IsAlive);
        Assert.False(probe.Context.IsAlive);
        Assert.False(harness.Provider.Completion.Task.IsCompleted);

        harness.Provider.Complete();
        harness.Scheduler.WaitForPending();
        Assert.Equal(1, harness.Scheduler.RunPending());

        // The result's snapshot is gone, so it is not applied anywhere; the
        // current snapshot gets a fresh visible pass instead.
        Assert.Equal(1, harness.Host.VisiblePasses);
        Assert.Equal(0, harness.Host.CanvasInvalidations);
        await harness.Highlighting.Retire(restart: false).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CompletedRequestForTheLiveSnapshotIsCachedAndApplied()
    {
        // The same request path with the snapshot kept: the result is cached,
        // and the next visible pass applies it to the block it was made for.
        var harness = new HighlightHarness();
        await harness.Highlighting.Restart();
        (LayoutSnapshot snapshot, CodeBlockBox block) = CreateCodeSnapshot();
        try
        {
            harness.Published.Snapshot = snapshot;
            harness.Highlighting.ScheduleVisible();
            Assert.Equal(1, harness.Provider.Requests);

            harness.Provider.Complete();
            harness.Scheduler.WaitForPending();
            Assert.Equal(1, harness.Scheduler.RunPending());
            Assert.Equal(1, harness.Host.VisiblePasses);
            Assert.Equal(0, harness.Host.CanvasInvalidations);

            harness.Highlighting.ScheduleVisible();
            Assert.Equal(1, harness.Provider.Requests);
            Assert.Equal(1, harness.Host.CanvasInvalidations);
            Assert.True(block.HasAppliedSyntaxHighlighting(CodeBlockHighlightTargets.CreateCacheKey(
                block, MarkdownThemeVariant.Light, RuntimeHelpers.GetHashCode(harness.Provider),
                harness.Provider.Revision)));
        }
        finally
        {
            await harness.Highlighting.Retire(restart: false).WaitAsync(TimeSpan.FromSeconds(10));
            harness.Published.Snapshot = null;
            snapshot.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SnapshotProbe ScheduleOnUnreferencedSnapshot(HighlightHarness harness)
    {
        (LayoutSnapshot snapshot, CodeBlockBox block) = CreateCodeSnapshot();
        harness.Published.Snapshot = snapshot;
        harness.Highlighting.ScheduleVisible();
        // The view publishes a replacement; nothing else holds the old graph.
        harness.Published.Snapshot = null;
        return new SnapshotProbe(
            new WeakReference(snapshot), new WeakReference(block), new WeakReference(block.Context));
    }

    private static (LayoutSnapshot Snapshot, CodeBlockBox Block) CreateCodeSnapshot()
    {
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(), CreateTheme(), new MarkdownSourceMap(Code),
            new MarkdownExtensionRegistry(), FlowDirection.LeftToRight);
        var metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, Code.Length - 1), Code, "csharp", new Dictionary<string, string>());
        var block = new CodeBlockBox(context, metadata, Code, false, false) { BlockIndex = 0 };
        float height = block.Measure(300);
        block.Arrange(0, 0, 300);
        return (new LayoutSnapshot([block], new MarkdownSourceMap(Code), width: 300, height: height), block);
    }

    private static ThemeSnapshot CreateTheme()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.CodeBlock] = style,
        };
        Color white = Color.FromArgb(255, 255, 255, 255);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            white, white, white, white,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
    }

    private sealed record SnapshotProbe(WeakReference Snapshot, WeakReference Block, WeakReference Context);

    /// <summary>A loaded highlighting owner wired to a recording host, a manual UI thread and a held provider.</summary>
    private sealed class HighlightHarness
    {
        internal HighlightHarness()
        {
            var lifetime = new MarkdownViewLifetime();
            lifetime.MarkLoaded();
            Host = new HighlightHost(Scheduler, Provider, CreateTheme());
            Highlighting = new CodeBlockHighlighting(Host, lifetime, Published);
        }

        internal ManualUiScheduler Scheduler { get; } = new();

        internal HeldProvider Provider { get; } = new();

        internal PublishedLayout Published { get; } = new();

        internal HighlightHost Host { get; }

        internal CodeBlockHighlighting Highlighting { get; }
    }

    /// <summary>A provider whose one result the test releases by hand.</summary>
    private sealed class HeldProvider : ICodeHighlighter
    {
        internal TaskCompletionSource<CodeBlockHighlightResult?> Completion { get; } = new();

        internal int Requests { get; private set; }

        internal string? LastCode { get; private set; }

        public int Revision => 1;

        public ValueTask<CodeBlockHighlightResult?> HighlightAsync(
            CodeBlockHighlightRequest request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastCode = request.Code;
            return new ValueTask<CodeBlockHighlightResult?>(Completion.Task);
        }

        internal void Complete() =>
            Completion.SetResult(new CodeBlockHighlightResult(
                [new CodeBlockHighlightSpan(0, 3, Color.FromArgb(255, 1, 2, 3))]));
    }

    private sealed class HighlightHost(
        IMarkdownUiScheduler scheduler,
        ICodeHighlighter provider,
        ThemeSnapshot theme) : ICodeBlockHighlightingHost
    {
        internal int VisiblePasses { get; private set; }

        internal int CanvasInvalidations { get; private set; }

        public object DocumentOwner { get; } = new();

        public IMarkdownUiScheduler? UiScheduler => scheduler;

        public bool HasMeasuredCodeBlocks => true;

        public ICodeHighlighter? CodeHighlighter => provider;

        public bool IsCodeBlockSyntaxHighlightingEnabled => true;

        public IMarkdownPerformanceSession? PerformanceSession => null;

        public ThemeSnapshot? ThemeSnapshot => theme;

        public double LazyScrollVelocityPixelsPerSecond => 0;

        public bool TryGetViewport(out double top, out double height)
        {
            top = 0;
            height = 600;
            return true;
        }

        public void ScheduleVisibleCodeBlockHighlighting() => VisiblePasses++;

        public void InvalidateCanvas() => CanvasInvalidations++;

        public Task RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime) =>
            cts is null ? Task.CompletedTask : CancellationTokenSourceRetirement.CancelAndDisposeAfter(cts, lifetime);
    }
}
