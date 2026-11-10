using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Engine.Internal;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm.Internal.Compatibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Microsoft.UI.Xaml;
using Markdig.Syntax;
using Windows.UI;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <remarks>
/// This file is longer than 500 lines because it is one inventory: every
/// compact internal payload carrier's constructor values are listed here so a
/// new carrier is added beside the others. The carriers' fields are
/// <c>readonly</c>; the compiler enforces that at every write site.
/// </remarks>
public sealed class ImmutablePayloadStorageTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void InternalPayloadCarriersAreNotPublicApi()
    {
        Type[] carriers =
        [
            typeof(SharedLayoutMetricsKey), typeof(MarkdownExtensionRegistration), typeof(MarkdownTextFormatCache),
            typeof(MarkdownTextStyleRun), typeof(MarkdownSemanticNode), typeof(MarkdownSemanticDocument),
            typeof(InlineEmbedRun), typeof(InlineEmbedAutomationMetadata), typeof(DeclarativeHostedElementBox),
            typeof(MarkdownLayoutContext), typeof(SafeHtmlInlineScope), typeof(ViewportRange),
            typeof(LazyLayoutBand), typeof(VectorViewportTransform), typeof(PlatformThemeColorOverrides),
            typeof(PlatformThemeColors), typeof(EmbedBox), typeof(MarkdownLocalizationSnapshot),
            typeof(HostedElementRealizationGate<>.Operation), typeof(SafeHtmlRenderPolicy), typeof(ThemeSnapshot),
            typeof(ThemeStyleCacheKey), typeof(SharedCanvasBitmapCacheKey), typeof(InlineScenePlacementRollbackState),
            typeof(InlineMeasureCheckpoint), typeof(MarkdownLayoutRetirementFailureSummary),
            typeof(Gfm029Compatibility.PreparedSource), typeof(Gfm029SourceLine), typeof(MarkdownRebuildDispatchState),
            typeof(MarkdownTargetCommand), typeof(MarkdownLinkActivationDecision), typeof(MarkdownTableClipboardPayload),
            typeof(MarkdownAutomationTextFingerprint), typeof(MarkdownHighContrastStyleRoles), typeof(SafeHtmlTag),
            typeof(AbbreviationRun), typeof(LinkRun), typeof(InlineImageRun), typeof(InlineVectorSceneRun),
            typeof(InlineSceneGeometry), typeof(FocusableItem), typeof(LoadCompletedEventArgs),
            typeof(SharedCanvasBitmapCacheEntry), typeof(SharedCanvasTextFormatEntry), typeof(SharedLayoutMetrics),
            typeof(LayoutSnapshot), typeof(IntrinsicWidthMetrics), typeof(VectorSceneBox),
            typeof(MarkdownEnvironmentMonitor.Subscription), typeof(ParsedMarkdown), typeof(HostedElementFallbackKey),
            typeof(MarkdownRenderer.Images.MarkdownImagePreparationAdmission), typeof(TextElementBoundaryIndex),
            typeof(LayoutSnapshotCommittedMetadata), typeof(FocusablePlanEntry), typeof(LazyLayoutCommit),
            typeof(SnapshotScrollAnchor), typeof(CodeBlockBox), typeof(ListItemBox), typeof(InlineContainerBox),
            typeof(ImageBox), typeof(CompiledMarkdownStyleSheet), typeof(SvgIntrinsics.SvgRootGeometry),
            typeof(TableBox.CellInfo), typeof(MarkdownRuntimeExtensionSet), typeof(MarkdownCompletedParse),
            typeof(MarkdownInFlightParse),
        ];
        foreach (Type carrier in carriers)
            Assert.False(carrier.IsVisible, carrier.Name); // None of these carriers is a public API.
    }

    [Fact]
    public void ContextMenuTargetStoresDetachedValuesAndOnlyItsBitmapLease()
    {
        Type type = typeof(ContextMenuTarget);
        Assert.False(type.IsVisible);
        var tableSnapshot = new MarkdownTableClipboardSnapshot([["cell"]], 0);
        var target = new ContextMenuTarget
        {
            LinkUrl = "https://example.test/link",
            LinkSourceRange = new SourceSpan(1, 2),
            ImageUrl = "https://example.test/image.png",
            ImageSourceRange = new SourceSpan(3, 17),
            CodeText = "code",
            CodeLanguage = "cs",
            CodeSourceRange = new SourceSpan(5, 6),
            TableSnapshot = tableSnapshot,
            TableSourceRange = new SourceSpan(7, 8),
        };

        Assert.Equal("https://example.test/link", target.LinkUrl);
        Assert.Equal(new SourceSpan(1, 2), target.LinkSourceRange);
        Assert.Equal("https://example.test/image.png", target.ImageUrl);
        Assert.Equal(new SourceSpan(3, 17), target.ImageSourceRange);
        Assert.Equal("code", target.CodeText);
        Assert.Equal("cs", target.CodeLanguage);
        Assert.Equal(new SourceSpan(5, 6), target.CodeSourceRange);
        Assert.Same(tableSnapshot, target.TableSnapshot);
        Assert.Equal(new SourceSpan(7, 8), target.TableSourceRange);
        Assert.Null(target.ImageBitmapLease);
    }

    [Fact]
    public void PopulatedContextMenuTargetsRetainNoLiveLayout()
    {
        // Targets built from a link, a code block and a table keep detached
        // values only: the snapshot, its context and every box it hit are
        // collectable while the targets are alive.
        (ContextMenuTarget[] targets, WeakReference[] layout) = PopulateTargetsFromUnrootedLayout();

        RetentionCollector.CollectUntilDead(layout);

        Assert.All(layout, reference => Assert.False(reference.IsAlive));
        Assert.Equal("https://example.test/link", targets[0].LinkUrl);
        Assert.Contains("detached code", targets[1].CodeText, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(targets[1].CodeLanguage));
        MarkdownTableClipboardSnapshot table = Assert.IsType<MarkdownTableClipboardSnapshot>(targets[2].TableSnapshot);
        Assert.Contains("cell", table.Format().Text, StringComparison.Ordinal);
        foreach (ContextMenuTarget target in targets)
            target.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ContextMenuTarget[] Targets, WeakReference[] Layout) PopulateTargetsFromUnrootedLayout()
    {
        const string source =
            "[link](https://example.test/link)\n\n" +
            "```cs\ndetached code\n```\n\n" +
            "| head | other |\n|---|---|\n| cell | value |\n";
        var layout = new List<WeakReference>();
        var targets = new ContextMenuTarget[3];
        using (LayoutSnapshot snapshot = SafeHtmlRenderingFixture.BuildGitHub(source))
        {
            BlockBox[] boxes = SafeHtmlRenderingFixture.FlattenBoxes(snapshot).ToArray();
            InlineContainerBox link = boxes.OfType<InlineContainerBox>().First(box => box.Runs.OfType<LinkRun>().Any());
            BlockBox code = boxes.OfType<CodeBlockBox>().Single();
            BlockBox tableBox = boxes.OfType<TableBox>().Single();
            BlockBox[] hits = [link, code, tableBox];
            for (int index = 0; index < hits.Length; index++)
            {
                Rect bounds = hits[index].Bounds;
                var point = new Point(bounds.X + 2, bounds.Y + bounds.Height / 2);
                targets[index] = new ContextMenuTarget();
                Assert.True(ContextMenuTargets.PopulateContextMenuTarget(hits[index], point, targets[index]));
            }

            layout.Add(new WeakReference(snapshot));
            layout.Add(new WeakReference(link.Context));
            foreach (BlockBox box in boxes)
                layout.Add(new WeakReference(box));
        }

        return (targets, layout.ToArray());
    }

    [Fact]
    public void ContextMenuTableSnapshotHasTextMatchesFormattedPayload()
    {
        (string[][] Rows, int HeaderRows)[] cases =
        [
            (Array.Empty<string[]>(), 0),
            (new string[][] { Array.Empty<string>() }, 0),
            (new string[][] { new[] { string.Empty } }, 0),
            (new string[][] { new[] { "value" } }, 0),
            (new string[][] { new[] { string.Empty, string.Empty } }, 0),
            (new string[][] { new[] { string.Empty }, new[] { string.Empty } }, 0),
        ];

        foreach ((string[][] rows, int headerRows) in cases)
        {
            var snapshot = new MarkdownTableClipboardSnapshot(rows, headerRows);
            MarkdownTableClipboardPayload payload = snapshot.Format();
            Assert.Equal(payload.Text.Length > 0, snapshot.HasText);
        }
    }

    [Fact]
    public void ContextMenuExecutionCloneKeepsBitmapAliveAfterMenuTargetDisposes()
    {
        using var device = new CanvasDevice();
        var menuTarget = new ContextMenuTarget { ImageUrl = "https://example.test/image.png" };
        SharedCanvasBitmapCache.Lease lease = _renderResources.Images.BitmapCache.StoreAndAcquire(
            device,
            $"context-menu-target-{Guid.NewGuid():N}",
            CreateBitmap(device));
        ContextMenuTarget? executionTarget = null;

        try
        {
            menuTarget.SetImageBitmapLease(lease);
            executionTarget = menuTarget.CloneForExecution();
            Assert.NotNull(executionTarget);

            menuTarget.Dispose();
            Assert.Null(menuTarget.ImageBitmapLease);
            _renderResources.Images.BitmapCache.ReleaseDevice(device);

            SharedCanvasBitmapCache.Lease executionLease =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(executionTarget.ImageBitmapLease);
            CanvasBitmap clonedBitmap = executionLease.Bitmap;
            Assert.Equal(1u, clonedBitmap.SizeInPixels.Width);
            Assert.Equal(1u, clonedBitmap.SizeInPixels.Height);
            Assert.Equal("https://example.test/image.png", executionTarget.ImageUrl);
        }
        finally
        {
            (executionTarget ?? menuTarget).Dispose();
            _renderResources.Images.BitmapCache.ReleaseDevice(device);
        }
    }

    [Fact]
    public void AdditionalCompactPayloadsPreserveConstructorValuesAndReferences()
    {
        string abbreviationExpansion = new("expanded phrase".ToCharArray());
        var abbreviation = new AbbreviationRun("abbr", abbreviationExpansion);
        Assert.Equal("expanded phrase", abbreviation.Expansion);
        Assert.Same(abbreviationExpansion, abbreviation.Expansion);
        Assert.Equal("abbr (expanded phrase)", abbreviation.AccessibleText);

        string linkUrl = new("https://example.test/path".ToCharArray());
        string linkTitle = new("link title".ToCharArray());
        var link = new LinkRun("label", linkUrl, linkTitle);
        Assert.Equal("https://example.test/path", link.Url);
        Assert.Same(linkUrl, link.Url);
        Assert.Equal("link title", link.Title);
        Assert.Same(linkTitle, link.Title);
        Assert.Null(link.AccessibilityName);
        Assert.Null(link.DisclosureId);
        Assert.False(link.IsExpanded);

        string imageAlt = new("image alt".ToCharArray());
        string imageUrl = new("https://images.example.test/pixel.png".ToCharArray());
        string imageTitle = new("image title".ToCharArray());
        string linkedImageUrl = new("https://example.test/linked-image".ToCharArray());
        string linkedImageTitle = new("linked image title".ToCharArray());
        var image = new InlineImageRun(
            CreateContext(),
            imageAlt,
            imageUrl,
            imageTitle,
            linkedImageUrl,
            linkedImageTitle);
        try
        {
            Assert.Equal("https://images.example.test/pixel.png", image.Url);
            Assert.Same(imageUrl, image.Url);
            Assert.Equal("image title", image.Title);
            Assert.Same(imageTitle, image.Title);
            Assert.Equal("https://example.test/linked-image", image.LinkUrl);
            Assert.Same(linkedImageUrl, image.LinkUrl);
            Assert.Equal("linked image title", image.LinkTitle);
            Assert.Same(linkedImageTitle, image.LinkTitle);
            Assert.Equal("image alt", image.AltText);
            Assert.Same(imageAlt, image.AltText);
            Assert.NotNull(image.Image);
            Assert.True(image.IsLinked);
        }
        finally
        {
            image.Image.Dispose();
        }

        var widthMetrics = new IntrinsicWidthMetrics(12.5f, 42.25f);
        Assert.Equal(12.5f, widthMetrics.Minimum);
        Assert.Equal(42.25f, widthMetrics.Preferred);

        MarkdownLayoutContext context = CreateContext();
        var scene = new MarkdownVectorScene(12, 8, 6, Array.Empty<MarkdownVectorCommand>());
        using (var vectorRun = new InlineVectorSceneRun(
                   context,
                   scene,
                   "semantic vector text",
                   "accessible vector name",
                   "accessible vector description",
                   MarkdownElementKeys.Math))
        {
            Assert.Equal("semantic vector text", vectorRun.SemanticText);
            Assert.Equal("accessible vector name", vectorRun.AccessibilityName);
            Assert.Equal("accessible vector description", vectorRun.AccessibilityDescription);
            Assert.Null(vectorRun.DeferredSceneMarker);
            Assert.Null(vectorRun.LinkUrl);
            Assert.Null(vectorRun.LinkTitle);

            Rect[] regions = [new Rect(1, 2, 3, 4), new Rect(5, 6, 7, 8)];
            var geometry = new InlineSceneGeometry(vectorRun, regions);
            Assert.Same(vectorRun, geometry.Run);
            Assert.Equal(2, geometry.RegionCount);
            Assert.Equal(regions[0], geometry.GetRegionBounds(0));
            Assert.Equal(regions[1], geometry.GetRegionBounds(1));
        }

        var focusable = new FocusableItem(7, 9, FocusableItemKind.VectorSemantic);
        Assert.Equal(7, focusable.BlockIndex);
        Assert.Equal(9, focusable.InlineIndex);
        Assert.Equal(FocusableItemKind.VectorSemantic, focusable.Kind);
        Assert.Equal(focusable, new FocusableItem(7, 9, FocusableItemKind.VectorSemantic));

        var loadCompleted = new LoadCompletedEventArgs(layoutInvalidated: true);
        Assert.True(loadCompleted.LayoutInvalidated);
        Assert.False(new LoadCompletedEventArgs(layoutInvalidated: false).LayoutInvalidated);

        var layoutMetrics = new SharedLayoutMetrics(topLevelBlockCount: 2, estimatedKeyWeightBytes: 50);
        Assert.Equal(186L, layoutMetrics.EstimatedWeightBytes);
        Assert.Equal(2, layoutMetrics.TopLevelBlockCount);

        var boundaries = new TextElementBoundaryIndex("A😀");
        Assert.Equal(3, boundaries.TextLength);
        Assert.Equal((1, 3), boundaries.FindBoundaries(2));
    }

    [Fact]
    public void CompactSharedCacheEntriesPreserveConstructorValuesAndReferences()
    {
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        SharedCanvasBitmapCache cache = _renderResources.Images.BitmapCache;

        CanvasBitmap uncachedBitmap = CreateBitmap(device);
        SharedCanvasBitmapCacheEntry? uncachedEntry = null;
        try
        {
            uncachedEntry = new SharedCanvasBitmapCacheEntry(cache, uncachedBitmap, bitmapReservation: null);
            Assert.Same(uncachedBitmap, uncachedEntry.Bitmap);
            Assert.Equal(4L, uncachedEntry.WeightBytes);
            Assert.Equal(4L, uncachedEntry.PixelWeightBytes);
            Assert.Null(uncachedEntry.IntrinsicSize);
            Assert.Equal(1, uncachedEntry.PixelWidth);
            Assert.Equal(1, uncachedEntry.PixelHeight);
        }
        finally
        {
            if (uncachedEntry is null)
                uncachedBitmap.Dispose();
            else
                uncachedEntry.Release();
        }

        CanvasBitmap cachedBitmap = CreateBitmap(device);
        SharedCanvasBitmapCacheEntry? cachedEntry = null;
        try
        {
            cachedEntry = new SharedCanvasBitmapCacheEntry(
                cache,
                cachedBitmap,
                intrinsicSize: new Size(24, 16),
                knownPixelWidth: 3,
                knownPixelHeight: 4,
                svgWorkId: 11L,
                svgSourceBytes: 12L,
                svgTargetPixels: 13L,
                bitmapReservation: null);
            Assert.Same(cachedBitmap, cachedEntry.Bitmap);
            Assert.Equal(48L, cachedEntry.WeightBytes);
            Assert.Equal(new Size(24, 16), cachedEntry.IntrinsicSize);
            Assert.Equal(3, cachedEntry.PixelWidth);
            Assert.Equal(4, cachedEntry.PixelHeight);
        }
        finally
        {
            if (cachedEntry is null)
                cachedBitmap.Dispose();
            else
                cachedEntry.Release();
        }

        var textRetirement = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord textRetirementRecord = textRetirement.Reserve(0, 1);
        var format = new CanvasTextFormat();
        textRetirementRecord.SetTemporary(0, format);
        var textEntry = new SharedCanvasTextFormatEntry(format, 0L, textRetirementRecord);
        try
        {
            Assert.Same(format, textEntry.Format);
            Assert.Equal(1L, textEntry.WeightBytes);
        }
        finally
        {
            _ = textEntry.Release();
        }
    }

    [Fact]
    public void CompactViewportPayloadsPreserveValuesAndTheMutableCommitFlag()
    {
        var commit = new LazyLayoutCommit(true, 17, 120, 340, 29);
        var resourcesChanged = commit with { NativeSceneResourcesChanged = true };
        Assert.True(resourcesChanged.Changed);
        Assert.Equal(17, resourcesChanged.MeasuredBlocks);
        Assert.Equal(120, resourcesChanged.OldHeight);
        Assert.Equal(340, resourcesChanged.NewHeight);
        Assert.Equal(29, resourcesChanged.LayoutRevision);
        Assert.True(resourcesChanged.NativeSceneResourcesChanged);
        Assert.False(commit.NativeSceneResourcesChanged);

        var anchor = new SnapshotScrollAnchor(4, 12.5, 200);
        Assert.Equal(4, anchor.BlockIndex);
        Assert.Equal(12.5, anchor.OffsetFromTop);
        Assert.Equal(200, anchor.Height);

        var metadata = new LayoutSnapshotCommittedMetadata(new Size(640, 480), 37L, 21);
        Assert.Equal(new Size(640, 480), metadata.Size);
        Assert.Equal(37L, metadata.Revision);
        Assert.Equal(21, metadata.MeasuredCount);

        var geometry = new SvgIntrinsics.SvgRootGeometry(100, 50, 5, 3, 200, 100);
        Assert.Equal(5, geometry.ViewBoxX);
        Assert.Equal(3, geometry.ViewBoxY);
        Assert.True(geometry.HasIntrinsicSize);
        Assert.True(geometry.HasViewBox);
        Assert.Equal(0.5, geometry.UserUnitToViewportScaleX);
        Assert.Equal(0.5, geometry.UserUnitToViewportScaleY);
    }

    [Fact]
    public void CommittedMetadataSizeCanOnlyBeCompletedOnceBeforePublication()
    {
        var metadata = new LayoutSnapshotCommittedMetadata(new Size(640, 480), 37L, 21);
        Size finalSize = new(1280, 720);

        metadata.CompleteSizeBeforePublish(finalSize);
        Assert.Equal(finalSize, metadata.Size);
        Assert.Equal(37L, metadata.Revision);
        Assert.Equal(21, metadata.MeasuredCount);

        Assert.Throws<InvalidOperationException>(() => metadata.CompleteSizeBeforePublish(new Size(1, 1)));
        Assert.Equal(finalSize, metadata.Size);

        metadata.MarkPublished();
        Parallel.For(0, 64, _ => Assert.Equal(finalSize, metadata.Size));

        Assert.Throws<InvalidOperationException>(() => metadata.CompleteSizeBeforePublish(new Size(2, 2)));
        Assert.Throws<InvalidOperationException>(metadata.MarkPublished);
        Assert.Equal(finalSize, metadata.Size);
    }

    [Fact]
    public void CompactLayoutSnapshotPayloadsPreserveCapturedReferences()
    {
        IReadOnlyList<BlockBox> blocks = Array.Empty<BlockBox>();
        var sourceMap = new MarkdownSourceMap("immutable source");
        using var snapshot = new LayoutSnapshot(blocks, sourceMap, 320, 240);

        Assert.Same(blocks, snapshot.Blocks);
        Assert.Same(sourceMap, snapshot.SourceMap);
        Assert.Equal(MarkdownAutomationTextFingerprint.Compute("immutable source"), snapshot.SourceFingerprint);
        Assert.NotNull(snapshot.SemanticDocument);
    }

    [Fact]
    public void CompactPrivateRangeAndRetirementPayloadsPreserveConstructorValues()
    {
        var range = new RasterIconHeaderReader.IconPayloadRange(31, 17);
        Assert.Equal(31, range.Offset);
        Assert.Equal(17, range.Length);
        Assert.Equal(48, range.End);

        var failure = new InvalidOperationException("retained failure");
        var retirement = new SnapshotRetirementTracker.RetirementFailure(42L, failure);
        Assert.Equal(42L, retirement.Sequence);
        Assert.Same(failure, retirement.Exception);

        Exception[] failures = [failure];
        var cursor = new BlockBoxCleanupScope.AggregateFailureCursor(failures, 3, 5);
        Assert.Same(failures, cursor.InnerExceptions);
        Assert.Equal(3, cursor.NextIndex);
        Assert.Equal(5, cursor.Depth);

        int[] insertions = [5, 12];
        int[] maskedSeparators = [8];
        var preparedSource = new Gfm029Compatibility.PreparedSource("rewritten source", insertions, maskedSeparators);
        Assert.Equal("rewritten source", preparedSource.Text);
        Assert.True(preparedSource.HasChanges);
        // The views alias the constructor's arrays: span equality is identity
        // of the first element and the length, so a copy would fail it.
        Assert.True(preparedSource.Insertions == insertions);
        Assert.True(preparedSource.MaskedSeparators == maskedSeparators);
        Assert.False(new Gfm029Compatibility.PreparedSource(
            "unchanged source", Array.Empty<int>(), Array.Empty<int>()).HasChanges);
        Assert.True(new Gfm029Compatibility.PreparedSource(
            "inserted source", insertions, Array.Empty<int>()).HasChanges);
        Assert.True(new Gfm029Compatibility.PreparedSource(
            "masked source", Array.Empty<int>(), maskedSeparators).HasChanges);

        var lineInfo = new Gfm029SourceLine(27, 14);
        Assert.Equal(27, lineInfo.Start);
        Assert.Equal(14, lineInfo.Length);

        var dispatchState = new MarkdownRebuildDispatchState(91L, MarkdownRebuildDispatchPriority.Normal);
        Assert.Equal(91L, dispatchState.Ticket);
        Assert.Equal(MarkdownRebuildDispatchPriority.Normal, dispatchState.Priority);

        var command = new MarkdownTargetCommand(MarkdownCommandKind.Activate, new SourceSpan(6, 11), "target", "cs");
        Assert.Equal(MarkdownCommandKind.Activate, command.Kind);
        Assert.Equal(new SourceSpan(6, 11), command.SourceRange);
        Assert.Equal("target", command.Target);
        Assert.Equal("cs", command.Language);

        var decision = new MarkdownLinkActivationDecision(
            MarkdownLinkInputKind.Touch, MarkdownLinkActivationPolicyResult.DisabledCommand);
        Assert.Equal(MarkdownLinkInputKind.Touch, decision.InputKind);
        Assert.Equal(MarkdownLinkActivationPolicyResult.DisabledCommand, decision.Result);
        Assert.True(decision.IsHandled);

        var clipboard = new MarkdownTableClipboardPayload("cell text", "<table></table>");
        Assert.Equal("cell text", clipboard.Text);
        Assert.Equal("<table></table>", clipboard.Html);

        byte[] digest = new byte[32];
        MarkdownAutomationTextFingerprint fingerprint = MarkdownAutomationTextFingerprint.FromSha256(23, digest);
        Assert.Equal(23, fingerprint.Length);

        var highContrast = new MarkdownHighContrastStyleRoles(
            MarkdownHighContrastColorRole.Hotlight, MarkdownHighContrastColorRole.Window,
            MarkdownHighContrastColorRole.HighlightText, true, false);
        Assert.Equal(MarkdownHighContrastColorRole.Hotlight, highContrast.Foreground);
        Assert.Equal(MarkdownHighContrastColorRole.Window, highContrast.Background);
        Assert.Equal(MarkdownHighContrastColorRole.HighlightText, highContrast.AccentBar);
        Assert.True(highContrast.Underline);
        Assert.False(highContrast.Strikethrough);

        var safeHtmlAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "safe",
        };
        var safeHtmlTag = new SafeHtmlTag(SafeHtmlTagKind.SelfClosing, "img", safeHtmlAttributes, 41, 8);
        Assert.Equal(SafeHtmlTagKind.SelfClosing, safeHtmlTag.Kind);
        Assert.Equal("img", safeHtmlTag.Name);
        Assert.Same(safeHtmlAttributes, safeHtmlTag.Attributes);
        Assert.Equal(41, safeHtmlTag.SourceStart);
        Assert.Equal(8, safeHtmlTag.SourceLength);

        var summarized = new MarkdownLayoutRetirementFailureSummary(failure);
        Assert.Equal(failure.GetType().FullName, summarized.ExceptionType);
        Assert.Equal(failure.HResult, summarized.HResultCode);
        Assert.Null(summarized.OmittedFailureCount);
        var omitted = new MarkdownLayoutRetirementFailureSummary(4);
        Assert.Null(omitted.ExceptionType);
        Assert.Null(omitted.HResultCode);
        Assert.Equal(4, omitted.OmittedFailureCount);
    }

    [Fact]
    public void CompactImmutablePayloadsPreserveValuesAndBehavior()
    {
        AssertExtensionRegistrationsKeepTheirPayloads();
        AssertSemanticDocumentFormatCacheAndStyleRunPayloads();

        var metadata = new InlineEmbedAutomationMetadata(
            "checked", "unchecked", "read-only", "toggle", "task-item", isChecked: false,
            canSetState: static _ => true,
            trySetState: static _ => true);
        Assert.Equal("checked", metadata.CheckedName);
        Assert.Equal("unchecked", metadata.UncheckedName);
        Assert.Equal("read-only", metadata.ReadOnlyHelpText);
        Assert.Equal("toggle", metadata.ToggleHelpText);
        Assert.Equal("task-item", metadata.AutomationId);
        Assert.Equal("unchecked", metadata.CurrentName);
        Assert.Equal("toggle", metadata.CurrentHelpText);
        Assert.True(metadata.TrySetState(true));
        Assert.Equal("checked", metadata.CurrentName);

        var policy = new SafeHtmlRenderPolicy(
            enableLinks: true,
            enableImages: false,
            renderUnknownElementsLiterally: true,
            allowedStyleClasses: ["safe-class"]);
        Assert.True(policy.EnableLinks);
        Assert.False(policy.EnableImages);
        Assert.True(policy.RenderUnknownElementsLiterally);
        Assert.Equal(SafeHtmlParseLimits.Default, policy.Limits);
        Assert.True(policy.IsStyleClassAllowed("safe-class"));
        Assert.False(policy.IsStyleClassAllowed("other-class"));

        Func<Microsoft.UI.Xaml.FrameworkElement> elementFactory = static () => null!;
        var inlineRun = new InlineEmbedRun(24, 18, elementFactory);
        Assert.Equal(24f, inlineRun.DesiredWidth);
        Assert.Equal(18f, inlineRun.DesiredHeight);
        Assert.Same(elementFactory, inlineRun.ElementFactory);
    }

    private static void AssertExtensionRegistrationsKeepTheirPayloads()
    {
        MarkdownNodeRenderer synchronous = static (_, _) => { };
        MarkdownAsyncNodeRenderer asynchronous = static (_, _) => ValueTask.CompletedTask;
        var factory = new MarkdownOwnedExtensionFactory("owned", static () => null!);

        MarkdownExtensionRegistration extension = MarkdownExtensionRegistration.Extension("gfm");
        Assert.Equal(MarkdownExtensionRegistrationKind.Extension, extension.Kind);
        Assert.Equal("gfm", extension.Identifier);
        Assert.Null(extension.SynchronousRenderer);
        Assert.Null(extension.AsynchronousRenderer);
        Assert.Null(extension.OwnedFactory);

        MarkdownExtensionRegistration sync = MarkdownExtensionRegistration.SynchronousInline(
            "inline", synchronous);
        Assert.Equal(MarkdownExtensionRegistrationKind.SynchronousInline, sync.Kind);
        Assert.Equal("inline", sync.Identifier);
        Assert.Same(synchronous, sync.SynchronousRenderer);

        MarkdownExtensionRegistration asynchronousRegistration = MarkdownExtensionRegistration.AsynchronousBlock(
            "async", asynchronous);
        Assert.Equal(MarkdownExtensionRegistrationKind.AsynchronousBlock, asynchronousRegistration.Kind);
        Assert.Equal("async", asynchronousRegistration.Identifier);
        Assert.Same(asynchronous, asynchronousRegistration.AsynchronousRenderer);

        MarkdownExtensionRegistration owned = MarkdownExtensionRegistration.Owned(factory);
        Assert.Equal(MarkdownExtensionRegistrationKind.OwnedFactory, owned.Kind);
        Assert.Same(factory, owned.OwnedFactory);
    }

    private static void AssertSemanticDocumentFormatCacheAndStyleRunPayloads()
    {
        MarkdownSemanticDocument document = MarkdownSemanticDocument.Empty;
        Assert.Equal(string.Empty, document.Text);
        Assert.Equal(MarkdownAutomationTextFingerprint.Compute(string.Empty), document.TextProjectionFingerprint);
        Assert.Equal(0, document.TextElementBoundaries.TextLength);
        Assert.Empty(document.TextSpans);
        Assert.Equal(MarkdownSemanticRole.Document, document.Root.Role);
        Assert.Null(document.Root.Box);
        MarkdownTextFormatCache formatCache = MarkdownTextFormatCache.Create(document, theme: null);
        Assert.Same(document, formatCache.Document);
        Assert.Null(formatCache.Theme);

        var style = new ElementStyle();
        var styleRun = new MarkdownTextStyleRun(2, 9, "Emphasis", true, false, style);
        Assert.Equal("Emphasis", styleRun.ElementKey);
        Assert.True(styleRun.IsSubscript);
        Assert.False(styleRun.IsSuperscript);
        Assert.Same(style, styleRun.Style);
    }

    [Fact]
    public void CompactHostedElementPayloadPreservesConstructorValues()
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MarkdownHostedElementAttributes.DesiredHeight] = "43",
            ["custom.value"] = "kept",
        };
        var builder = new MarkdownContentBuilder();
        builder.AddHostedElement(
            "sample.factory",
            new SourceSpan(4, 12),
            new MarkdownStyleRole("sample-hosted"),
            MarkdownAccessibilityRole.Group,
            attributes,
            accessibilityName: "Sample widget",
            accessibilityDescription: "Widget description",
            semanticText: "Widget semantic text");
        MarkdownContentFragment fragment = builder.Build();
        MarkdownContent content = Assert.Single(fragment.Items);
        var fallbackKey = new HostedElementFallbackKey(fragment);
        DeclarativeHostedElementBox box = new(CreateContext(), content, fallbackKey);

        Assert.Same(fragment, fallbackKey.Fragment);
        Assert.Equal(fallbackKey, new HostedElementFallbackKey(fragment));
        Assert.Equal("sample.factory", box.FactoryKey);
        Assert.Equal(new SourceSpan(4, 12), box.SourceRange);
        Assert.Same(content.Attributes, box.Attributes);
        Assert.Equal(MarkdownAccessibilityRole.Group, box.AccessibilityRole);
        Assert.Equal("Sample widget", box.AccessibilityName);
        Assert.Equal("Widget description", box.AccessibilityDescription);
        Assert.Equal("Widget semantic text", box.SemanticText);
        Assert.Equal(fallbackKey, box.FallbackKey);
        Assert.Equal(43d, box.Measure(200) - box.Margin.Top - box.Margin.Bottom);

    }

    [Fact]
    public void CompactRendererSnapshotPayloadsPreserveConstructorValues()
    {
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        ThemeSnapshot theme = CreateTheme();
        var contextSourceMap = new MarkdownSourceMap("Hosted element source");
        var registry = new MarkdownExtensionRegistry();
        var context = new MarkdownLayoutContext(device, theme, contextSourceMap, registry, FlowDirection.LeftToRight);
        Assert.Same(device, context.ResourceCreator);
        Assert.Same(theme, context.ThemeSnapshot);
        Assert.Same(contextSourceMap, context.SourceMap);
        Assert.Same(registry, context.Registry);
        Assert.Equal(FlowDirection.LeftToRight, context.FlowDirection);
        Assert.Equal(CultureInfo.CurrentUICulture.Name, context.Language);
        Assert.Equal(context.Language, context.Culture.Name);
        Assert.Null(context.Dispatcher);

        string[] aliases = ["safe-class", "compact"];
        var scriptToken = new SafeHtmlScriptStyleAncestryToken("subscript");
        var inlineScope = new SafeHtmlInlineScope(
            "a", "Link", "https://example.test", "safe link", true, aliases, scriptToken);
        Assert.Equal("a", inlineScope.Name);
        Assert.Equal("Link", inlineScope.StyleKey);
        Assert.Equal("https://example.test", inlineScope.LinkUrl);
        Assert.Equal("safe link", inlineScope.LinkTitle);
        Assert.True(inlineScope.Suppressed);
        Assert.Same(aliases, inlineScope.StyleAliases);
        Assert.Same(scriptToken, inlineScope.ScriptToken);

        var blockScope = new SafeHtmlBlockScopeTracker.Scope("details", SafeHtmlAlignment.Center, true, 19);
        Assert.Equal("details", blockScope.Name);
        Assert.Equal(SafeHtmlAlignment.Center, blockScope.Alignment);
        Assert.True(blockScope.SuppressContent);
        Assert.Equal(19, blockScope.InlineFlowScopeId);

        var viewportRange = new ViewportRange(3, 8);
        Assert.Equal(3, viewportRange.Start);
        Assert.Equal(8, viewportRange.End);
        var lazyBand = new LazyLayoutBand(24.5, 97.25);
        Assert.Equal(24.5, lazyBand.Top);
        Assert.Equal(97.25, lazyBand.Bottom);
        var transform = new VectorViewportTransform(1.5f, 12f, -4f);
        Assert.Equal(1.5f, transform.Scale);
        Assert.Equal(12f, transform.TranslationX);
        Assert.Equal(-4f, transform.TranslationY);

        Color primary = Color.FromArgb(255, 12, 34, 56);
        Color secondary = Color.FromArgb(255, 21, 43, 65);
        Color accent = Color.FromArgb(255, 32, 54, 76);
        Color surface = Color.FromArgb(255, 43, 65, 87);
        var overrides = new PlatformThemeColorOverrides(primary, secondary, accent, surface);
        Assert.Equal(primary, overrides.PrimaryText);
        Assert.Equal(secondary, overrides.SecondaryText);
        Assert.Equal(accent, overrides.AccentText);
        Assert.Equal(surface, overrides.Surface);
        var colors = new PlatformThemeColors(primary, secondary, accent, surface);
        Assert.Equal(primary, colors.PrimaryText);
        Assert.Equal(secondary, colors.SecondaryText);
        Assert.Equal(accent, colors.AccentText);
        Assert.Equal(surface, colors.Surface);

        string parsedSource = new("parsed source".ToCharArray());
        var parsedAst = new Markdig.Syntax.MarkdownDocument();
        var parsedMarkdown = new ParsedMarkdown(parsedSource, parsedAst);
        Assert.Same(parsedSource, parsedMarkdown.SourceText);
        Assert.Same(parsedAst, parsedMarkdown.Document);

        var parsedBlock = new ParagraphBlock(null!);
        var factory = new TestEmbedFactory();
        var embedBox = new EmbedBox(parsedBlock, factory);
        Assert.Same(parsedBlock, embedBox.SourceBlock);
        Assert.Same(factory, embedBox.Factory);

        var stringProvider = new TestStringProvider();
        MarkdownLocalizationSnapshot localization = MarkdownLocalizationSnapshot.Capture(
            "fr-FR", environmentLanguage: null, stringProvider, CultureInfo.InvariantCulture)!;
        Assert.Equal("fr-FR", localization.Culture.Name);
        Assert.Same(stringProvider, localization.StringProvider);

        var request = new object();
        using (var operation = new HostedElementRealizationGate<object>.Operation(request, default))
            Assert.Same(request, operation.Request);

        var result = new CodeBlockHighlightResult(spans: null);
        var cache = new BoundedCodeBlockHighlightCache<string>(capacity: 2);
        cache.Set("payload-key", result);
        Assert.Equal(1, cache.Count);
        Assert.Equal(128L, cache.RetainedBytes);
        Assert.True(cache.TryGetValue("payload-key", out CodeBlockHighlightResult cached));
        Assert.Same(result, cached);
        // Evicting the oldest entry removes it by its stored key and subtracts its stored weight.
        cache.Set("second-key", new CodeBlockHighlightResult(spans: null));
        cache.Set("third-key", new CodeBlockHighlightResult(spans: null));
        Assert.False(cache.TryGetValue("payload-key", out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(256L, cache.RetainedBytes);
    }

    private MarkdownLayoutContext CreateContext() => new(
        CanvasDevice.GetSharedDevice(),
        CreateTheme(),
        new MarkdownSourceMap("Hosted element source"),
        new MarkdownExtensionRegistry(),
        FlowDirection.LeftToRight) { RenderResources = _renderResources };

    private static ThemeSnapshot CreateTheme()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            ["sample-hosted"] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }


    private static CanvasBitmap CreateBitmap(CanvasDevice device) => CanvasBitmap.CreateFromBytes(
        device,
        new byte[4],
        1,
        1,
        DirectXPixelFormat.B8G8R8A8UIntNormalized);


    private sealed class TestEmbedFactory : IMarkdownEmbedFactory
    {
        public bool CanCreate(Block block) => false;
        public float MeasureHeight(Block block, float availableWidth) => 0;
        public FrameworkElement CreateBlock(Block block) => throw new NotSupportedException();
    }

    private sealed class TestStringProvider : IMarkdownStringProvider
    {
        public string? GetString(string key, CultureInfo culture) => null;
    }
}
