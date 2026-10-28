using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Microsoft.UI.Xaml;
using Markdig.Syntax;
using Windows.UI;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImmutablePayloadStorageTests
{
    [Fact]
    public void InternalPayloadCompactionPreservesReadonlyStorage()
    {
        AssertReadonly(typeof(SharedLayoutMetricsKey),
            "DocumentIdentity WidthInSixtyFourthDips TypographyFingerprint FlowDirection CodeWrappingMode " +
            "LineNumberMode CodeCopyEnabled TaskEditingEnabled RasterScaleInThousandths Language " +
            "RegistryRevision DisclosureFingerprint ImageContext StyleSheet Registry EmbedFactory " +
            "HostedElementFactory ImageResolver SvgRenderer CommandProvider StringProvider");
        AssertReadonly(typeof(MarkdownExtensionRegistration),
            "Kind Identifier SynchronousRenderer AsynchronousRenderer OwnedFactory");
        AssertReadonly(typeof(MarkdownTextFormatCache), "Document Theme");
        AssertReadonly(typeof(MarkdownTextStyleRun),
            "ElementKey IsSubscript IsSuperscript Style");
        AssertReadonly(typeof(MarkdownSemanticNode), "Role Box");
        AssertReadonly(typeof(MarkdownSemanticDocument),
            "Root Text TextProjectionFingerprint SemanticRangeFingerprint TextElementBoundaries TextSpans");
        AssertReadonly(typeof(InlineEmbedRun), "DesiredWidth DesiredHeight ElementFactory");
        AssertReadonly(typeof(InlineEmbedAutomationMetadata),
            "CheckedName UncheckedName ReadOnlyHelpText ToggleHelpText AutomationId");
        AssertReadonly(typeof(DeclarativeHostedElementBox),
            "FactoryKey SourceRange Attributes AccessibilityRole AccessibilityName AccessibilityDescription " +
            "SemanticText FallbackKey");
        AssertReadonly(typeof(MarkdownLayoutContext),
            "ResourceCreator ThemeSnapshot SourceMap Registry FlowDirection Language Culture Dispatcher");
        AssertReadonly(Nested(typeof(SafeHtmlInlineState), "Scope"),
            "Name StyleKey LinkUrl LinkTitle Suppressed StyleAliases");
        AssertReadonly(Nested(typeof(SafeHtmlBlockScopeTracker), "Scope"),
            "Name Alignment SuppressContent InlineFlowScopeId");
        AssertReadonly(typeof(ViewportRange), "Start End");
        AssertReadonly(typeof(LazyLayoutBand), "Top Bottom");
        AssertReadonly(typeof(VectorViewportTransform), "Scale TranslationX TranslationY");
        AssertReadonly(Nested(typeof(ThemeResolver), "PlatformThemeColorOverrides"),
            "PrimaryText SecondaryText AccentText Surface");
        AssertReadonly(Nested(typeof(ThemeResolver), "PlatformThemeColors"),
            "PrimaryText SecondaryText AccentText Surface");
        AssertReadonly(typeof(EmbedBox), "SourceBlock Factory");
        AssertReadonly(typeof(MarkdownLocalizationSnapshot), "Culture StringProvider");
        AssertReadonly(Nested(typeof(BoundedCodeBlockHighlightCache<>), "Entry"), "Key Result Weight");
        AssertReadonly(Nested(typeof(HostedElementRealizationGate<>), "Operation"), "Request");
        AssertReadonly(typeof(SafeHtmlRenderPolicy),
            "EnableLinks EnableImages RenderUnknownElementsLiterally Limits");
        AssertReadonly(typeof(ThemeSnapshot),
            "SurfaceColor SelectionHighlightColor SelectionForegroundColor FocusVisualColor " +
            "IsDark IsHighContrast DocumentPadding LayoutFingerprint");
        AssertReadonly(Nested(typeof(ThemeSnapshot), "StyleCacheKey"),
            "ElementKey ContextKeys AliasKeys Language State");
        AssertReadonly(Nested(typeof(SharedCanvasBitmapCache), "CacheKey"), "Device Identity");
        AssertReadonly(Nested(typeof(InlineContainerBox), "InlineScenePlacementRollbackState"), "Run LocalBounds");
        AssertReadonly(Nested(typeof(ImageBox), "InlineMeasureCheckpoint"),
            "Owner Generation WasInlineLayout PreviousAvailableWidth PreviousImageWidth PreviousImageHeight " +
            "PreviousCaptionHeight PreviousBounds");
        AssertReadonly(Nested(typeof(RasterImageResourceBudget), "IconPayloadRange"), "Offset Length");
        AssertReadonly(Nested(typeof(ImageBox), "SvgRendererCacheIdentity"), "Value");
        AssertReadonly(Nested(typeof(SnapshotRetirementTracker), "RetirementFailure"), "Sequence Exception");
        AssertReadonly(Nested(typeof(DeferredInputQueue<>), "Entry"), "Value PointerId Coalescible");
        AssertReadonly(Nested(typeof(BlockBoxCleanupScope), "AggregateFailureCursor"), "InnerExceptions NextIndex Depth");
        AssertReadonly(Nested(typeof(WeightedLruCache<,>), "Entry"), "Key Value Weight");
        AssertReadonly(typeof(MarkdownLayoutRetirementFailureSummary),
            "ExceptionType HResultCode OmittedFailureCount");
        AssertReadonly(Nested(typeof(Gfm029Compatibility), "PreparedSource"), "Text HasChanges _insertions _maskedSeparators");
        AssertReadonly(Nested(typeof(Gfm029Compatibility), "LineInfo"), "Start Length");
        AssertReadonly(typeof(MarkdownRebuildDispatchState), "Ticket Priority");
        AssertReadonly(typeof(MarkdownTargetCommand), "Kind SourceRange Target Language");
        AssertReadonly(typeof(MarkdownLinkActivationDecision), "InputKind Result");
        AssertReadonly(typeof(MarkdownTableClipboardPayload), "Text Html");
        AssertReadonly(typeof(MarkdownAutomationTextFingerprint), "Length");
        AssertReadonly(typeof(MarkdownHighContrastStyleRoles),
            "Foreground Background AccentBar Underline Strikethrough");
        AssertReadonly(typeof(SafeHtmlTag), "Kind Name Attributes SourceStart SourceLength");
        AssertReadonly(typeof(AbbreviationRun), "Expansion");
        AssertReadonly(typeof(LinkRun), "Url Title");
        AssertReadonly(typeof(InlineImageRun), "Url Title LinkUrl LinkTitle AltText Image");
        AssertReadonly(typeof(InlineVectorSceneRun),
            "SemanticText AccessibilityName AccessibilityDescription");
        AssertReadonly(typeof(InlineSceneGeometry), "Run RegionCount");
        AssertReadonly(typeof(FocusableItem), "BlockIndex InlineIndex Kind");
        AssertReadonly(typeof(LoadCompletedEventArgs), "LayoutInvalidated");
        AssertReadonly(Nested(typeof(SharedCanvasBitmapCache), "Entry"),
            "Bitmap WeightBytes IntrinsicSize PixelWidth PixelHeight");
        AssertReadonly(Nested(typeof(SharedCanvasTextFormatCache), "Entry"), "Format WeightBytes");
        AssertReadonly(typeof(SharedLayoutMetrics), "EstimatedWeightBytes");
        AssertReadonly(typeof(LayoutSnapshot), "Blocks SourceMap SourceFingerprint SemanticDocument");
        AssertReadonly(typeof(IntrinsicWidthMetrics), "Minimum Preferred");
        AssertReadonly(typeof(VectorSceneBox), "LayoutContext SemanticItems ElementKey");
        AssertReadonly(Nested(typeof(MarkdownEnvironmentMonitor), "ListenerEntry"), "Id Listener");
        AssertReadonly(Nested(typeof(MarkdownEnvironmentMonitor), "Subscription"), "Snapshot");
        AssertReadonly(typeof(ParsedMarkdown), "SourceText Document");
        AssertReadonly(Nested(typeof(CompiledMarkdownStyleSheet), "CompiledRule"), "Style");
        AssertReadonly(typeof(HostedElementFallbackKey), "Fragment");
        AssertReadonly(typeof(MarkdownRenderer.Images.MarkdownImagePreparationAdmission), "CapacityBytes MaxConcurrency");
        AssertReadonly(Nested(typeof(HostedElementRealizationGate<>), "Operation"), "CancellationToken");
        AssertReadonly(Nested(typeof(SnapshotRetirementTracker), "FailureBucket"), "Details");
        AssertReadonly(typeof(TextElementBoundaryIndex), "TextLength");
        AssertReadonly(Nested(typeof(LayoutSnapshot), "CommittedMetadata"), "Revision MeasuredCount");
        PropertyInfo committedSize = Nested(typeof(LayoutSnapshot), "CommittedMetadata")
            .GetProperty("Size", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.NotNull(committedSize);
        Assert.Null(committedSize.SetMethod);
        AssertReadonly(Nested(typeof(LayoutSnapshot), "FocusablePlanEntry"), "Item HorizontalOverflow");
        AssertReadonly(typeof(LazyLayoutCommit), "Changed MeasuredBlocks OldHeight NewHeight LayoutRevision");
        AssertReadonly(typeof(SnapshotScrollAnchor), "BlockIndex OffsetFromTop Height");
        AssertReadonly(typeof(CodeBlockBox),
            "Metadata CodeLanguage CodeText LanguageDisplay HeaderText IsCopyButtonEnabled ShowLineNumbers Context");
        AssertReadonly(typeof(ListItemBox), "Marker Content");
        AssertReadonly(typeof(InlineContainerBox), "ElementKey Context");
        AssertReadonly(typeof(ImageBox), "Context Alt Url");
        AssertReadonly(typeof(CompiledMarkdownStyleSheet), "LayoutPolicy");
        AssertReadonly(typeof(SvgIntrinsics.SvgRootGeometry),
            "IntrinsicWidth IntrinsicHeight ViewBoxX ViewBoxY ViewBoxWidth ViewBoxHeight");
        AssertReadonly(typeof(TableBox.CellInfo), "Box Row Column IsHeader");
        AssertReadonly(Nested(typeof(MarkdownSourceMap.SourceProjection), "Emission"),
            "SourceStart SourceEnd RenderedLength");
        AssertReadonly(Nested(typeof(MarkdownSourceMap), "BlockEntryBounds"), "FirstEntryIndex LastEntryIndex");
        AssertReadonly(Nested(typeof(MarkdownSourceMap), "EntryIndex"), "Entries ReadOnlyEntries");
        AssertReadonly(Nested(typeof(MarkdownExtensionBuilder), "BuilderSnapshot"),
            "ExtensionIds Features BlockRenderers InlineRenderers SynchronousBlockRenderers " +
            "SynchronousInlineRenderers OwnedExtensionFactories Registrations");
        AssertReadonly(typeof(MarkdownRuntimeExtensionSet), "Extensions OwnedResources CallbackLifetime");
        AssertReadonly(Nested(typeof(MarkdownEngine), "CompletedParse"),
            "Document Key ParseGeneration WeightBytes");
        AssertReadonly(Nested(typeof(MarkdownEngine), "InFlightParse"),
            "CancellationToken Key Completion ParseGeneration SourceBytes");
    }

    [Fact]
    public void ContextMenuTargetStoresDetachedValuesAndOnlyItsBitmapLease()
    {
        Type type = Nested(typeof(MarkdownRendererControl), "ContextMenuTarget");
        Assert.False(type.IsVisible);
        object target = Activator.CreateInstance(type, nonPublic: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in new[]
        {
            "LinkUrl", "LinkSourceRange", "ImageUrl", "ImageSourceRange", "CodeText",
            "CodeLanguage", "CodeSourceRange", "TableSnapshot", "TableSourceRange",
        })
        {
            PropertyInfo property = type.GetProperty(name, flags)!;
            Assert.NotNull(property);
            Assert.True(property.CanWrite);
        }

        Type[] liveLayoutTypes =
        [
            typeof(BlockBox),
            typeof(InlineRun),
            typeof(ImageBox),
            typeof(CodeBlockBox),
            typeof(TableBox),
            typeof(LayoutSnapshot),
        ];
        foreach (FieldInfo field in type.GetFields(flags))
        {
            Type fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            Assert.DoesNotContain(liveLayoutTypes, liveType => liveType.IsAssignableFrom(fieldType));
        }

        SourceSpan source = new(3, 17);
        type.GetProperty("ImageSourceRange", flags)!.SetValue(target, source);
        Assert.Equal(source, type.GetProperty("ImageSourceRange", flags)!.GetValue(target));
        Assert.NotNull(type.GetField("_imageBitmapLease", flags));
        Assert.Null(Field(target, "_imageBitmapLease"));
    }

    [Fact]
    public void ContextMenuTableSnapshotHasTextMatchesFormattedPayload()
    {
        Type type = Nested(typeof(MarkdownRendererControl), "MarkdownTableClipboardSnapshot");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
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
            object snapshot = Construct(type, [rows, headerRows]);
            bool hasText = (bool)type.GetProperty("HasText", flags)!.GetValue(snapshot)!;
            MarkdownTableClipboardPayload payload = Assert.IsType<MarkdownTableClipboardPayload>(
                type.GetMethod("Format", flags)!.Invoke(snapshot, null));
            Assert.Equal(payload.Text.Length > 0, hasText);
        }
    }

    [Fact]
    public void ContextMenuExecutionCloneKeepsBitmapAliveAfterMenuTargetDisposes()
    {
        using var device = new CanvasDevice();
        Type type = Nested(typeof(MarkdownRendererControl), "ContextMenuTarget");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object menuTarget = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("ImageUrl", flags)!.SetValue(menuTarget, "https://example.test/image.png");
        SharedCanvasBitmapCache.Lease lease = SharedCanvasBitmapCache.StoreAndAcquire(
            device,
            $"context-menu-target-{Guid.NewGuid():N}",
            CreateBitmap(device));
        object? executionTarget = null;

        try
        {
            type.GetMethod("SetImageBitmapLease", flags)!.Invoke(menuTarget, [lease]);
            executionTarget = type.GetMethod("CloneForExecution", flags)!.Invoke(menuTarget, null);
            Assert.NotNull(executionTarget);

            type.GetMethod("Dispose", flags)!.Invoke(menuTarget, null);
            Assert.Null(type.GetProperty("ImageBitmapLease", flags)!.GetValue(menuTarget));
            SharedCanvasBitmapCache.ReleaseDevice(device);

            object executionLease = Assert.IsAssignableFrom<IDisposable>(
                type.GetProperty("ImageBitmapLease", flags)!.GetValue(executionTarget));
            CanvasBitmap clonedBitmap = (CanvasBitmap)executionLease.GetType()
                .GetProperty("Bitmap", flags)!.GetValue(executionLease)!;
            Assert.Equal(1u, clonedBitmap.SizeInPixels.Width);
            Assert.Equal(1u, clonedBitmap.SizeInPixels.Height);
            Assert.Equal(
                "https://example.test/image.png",
                type.GetProperty("ImageUrl", flags)!.GetValue(executionTarget));
        }
        finally
        {
            if (executionTarget is not null)
                type.GetMethod("Dispose", flags)!.Invoke(executionTarget, null);
            else
                type.GetMethod("Dispose", flags)!.Invoke(menuTarget, null);
            SharedCanvasBitmapCache.ReleaseDevice(device);
        }
    }

    [Fact]
    public void AdditionalCompactPayloadsPreserveConstructorValuesAndReferences()
    {
        string abbreviationExpansion = new("expanded phrase".ToCharArray());
        var abbreviation = new AbbreviationRun("abbr", abbreviationExpansion);
        Assert.Equal("expanded phrase", Field(abbreviation, "Expansion"));
        Assert.Same(abbreviationExpansion, Field(abbreviation, "Expansion"));
        Assert.Equal("abbr (expanded phrase)", abbreviation.AccessibleText);

        string linkUrl = new("https://example.test/path".ToCharArray());
        string linkTitle = new("link title".ToCharArray());
        var link = new LinkRun("label", linkUrl, linkTitle);
        Assert.Equal("https://example.test/path", Field(link, "Url"));
        Assert.Same(linkUrl, Field(link, "Url"));
        Assert.Equal("link title", Field(link, "Title"));
        Assert.Same(linkTitle, Field(link, "Title"));
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
            Assert.Equal("https://images.example.test/pixel.png", Field(image, "Url"));
            Assert.Same(imageUrl, Field(image, "Url"));
            Assert.Equal("image title", Field(image, "Title"));
            Assert.Same(imageTitle, Field(image, "Title"));
            Assert.Equal("https://example.test/linked-image", Field(image, "LinkUrl"));
            Assert.Same(linkedImageUrl, Field(image, "LinkUrl"));
            Assert.Equal("linked image title", Field(image, "LinkTitle"));
            Assert.Same(linkedImageTitle, Field(image, "LinkTitle"));
            Assert.Equal("image alt", Field(image, "AltText"));
            Assert.Same(imageAlt, Field(image, "AltText"));
            Assert.Same(image.Image, Field(image, "Image"));
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
            Assert.Equal("semantic vector text", Field(vectorRun, "SemanticText"));
            Assert.Equal("accessible vector name", Field(vectorRun, "AccessibilityName"));
            Assert.Equal("accessible vector description", Field(vectorRun, "AccessibilityDescription"));
            Assert.Null(vectorRun.DeferredSceneMarker);
            Assert.Null(vectorRun.LinkUrl);
            Assert.Null(vectorRun.LinkTitle);

            Rect[] regions = [new Rect(1, 2, 3, 4), new Rect(5, 6, 7, 8)];
            var geometry = new InlineSceneGeometry(vectorRun, regions);
            Assert.Same(vectorRun, Field(geometry, "Run"));
            Assert.Equal(2, Field(geometry, "RegionCount"));
            Assert.Equal(regions[0], geometry.GetRegionBounds(0));
            Assert.Equal(regions[1], geometry.GetRegionBounds(1));
        }

        var focusable = new FocusableItem(7, 9, FocusableItemKind.VectorSemantic);
        Assert.Equal(7, Field(focusable, "BlockIndex"));
        Assert.Equal(9, Field(focusable, "InlineIndex"));
        Assert.Equal(FocusableItemKind.VectorSemantic, Field(focusable, "Kind"));
        Assert.Equal(focusable, new FocusableItem(7, 9, FocusableItemKind.VectorSemantic));

        var loadCompleted = new LoadCompletedEventArgs(layoutInvalidated: true);
        Assert.Equal(true, Field(loadCompleted, "LayoutInvalidated"));
        Assert.False(new LoadCompletedEventArgs(layoutInvalidated: false).LayoutInvalidated);

        var layoutMetrics = new SharedLayoutMetrics(topLevelBlockCount: 2, estimatedKeyWeightBytes: 50);
        Assert.Equal(186L, Field(layoutMetrics, "EstimatedWeightBytes"));
        Assert.Equal(2, layoutMetrics.TopLevelBlockCount);

        var boundaries = new TextElementBoundaryIndex("A😀");
        Assert.Equal(3, Field(boundaries, "TextLength"));
        Assert.Equal((1, 3), boundaries.FindBoundaries(2));
    }

    [Fact]
    public void CompactSharedCacheEntriesPreserveConstructorValuesAndReferences()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type bitmapEntryType = Nested(typeof(SharedCanvasBitmapCache), "Entry");
        CanvasDevice device = CanvasDevice.GetSharedDevice();

        CanvasBitmap uncachedBitmap = CreateBitmap(device);
        object? uncachedEntry = null;
        try
        {
            uncachedEntry = Construct(bitmapEntryType, [uncachedBitmap, null!]);
            Assert.Same(uncachedBitmap, Field(uncachedEntry, "Bitmap"));
            Assert.Equal(4L, Field(uncachedEntry, "WeightBytes"));
            Assert.Equal(4L, Field(uncachedEntry, "PixelWeightBytes"));
            Assert.Null(Field(uncachedEntry, "IntrinsicSize"));
            Assert.Equal(1, Field(uncachedEntry, "PixelWidth"));
            Assert.Equal(1, Field(uncachedEntry, "PixelHeight"));
        }
        finally
        {
            if (uncachedEntry is null)
                uncachedBitmap.Dispose();
            else
                uncachedEntry.GetType().GetMethod("Release", flags)!.Invoke(uncachedEntry, null);
        }

        CanvasBitmap cachedBitmap = CreateBitmap(device);
        object? cachedEntry = null;
        try
        {
            ConstructorInfo cachedConstructor = Assert.Single(
                bitmapEntryType.GetConstructors(flags),
                constructor => constructor.GetParameters().Any(parameter => parameter.Name == "knownPixelWidth"));
            ParameterInfo[] parameters = cachedConstructor.GetParameters();
            var arguments = new object[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                arguments[index] = parameters[index].Name switch
                {
                    "bitmap" => cachedBitmap,
                    "intrinsicSize" => new Size(24, 16),
                    "knownPixelWidth" => 3,
                    "knownPixelHeight" => 4,
                    "svgWorkId" => 11L,
                    "svgSourceBytes" => 12L,
                    "svgTargetPixels" => 13L,
                    "bitmapReservation" => null!,
                    _ => throw new InvalidOperationException(
                        $"Unexpected {bitmapEntryType.Name} constructor parameter: {parameters[index].Name}"),
                };
            }

            cachedEntry = cachedConstructor.Invoke(arguments);
            Assert.Same(cachedBitmap, Field(cachedEntry, "Bitmap"));
            Assert.Equal(48L, Field(cachedEntry, "WeightBytes"));
            Assert.Equal(new Size(24, 16), Field(cachedEntry, "IntrinsicSize"));
            Assert.Equal(3, Field(cachedEntry, "PixelWidth"));
            Assert.Equal(4, Field(cachedEntry, "PixelHeight"));
        }
        finally
        {
            if (cachedEntry is null)
                cachedBitmap.Dispose();
            else
                cachedEntry.GetType().GetMethod("Release", flags)!.Invoke(cachedEntry, null);
        }

        Type textEntryType = Nested(typeof(SharedCanvasTextFormatCache), "Entry");
        var textRetirement = new NativeResourceRetirement(createTimer: false);
        NativeResourceRetirementRecord textRetirementRecord = textRetirement.Reserve(0, 1);
        var format = new CanvasTextFormat();
        textRetirementRecord.SetTemporary(0, format);
        object textEntry = Construct(textEntryType, [format, 0L, textRetirementRecord]);
        try
        {
            Assert.Same(format, Field(textEntry, "Format"));
            Assert.Equal(1L, Field(textEntry, "WeightBytes"));
        }
        finally
        {
            textEntry.GetType().GetMethod("Release", flags)!.Invoke(textEntry, null);
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

        object metadata = Construct(Nested(typeof(LayoutSnapshot), "CommittedMetadata"),
            [new Size(640, 480), 37L, 21]);
        Assert.Equal(new Size(640, 480), metadata.GetType()
            .GetProperty("Size", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(metadata));
        Assert.Equal(37L, Field(metadata, "Revision"));
        Assert.Equal(21, Field(metadata, "MeasuredCount"));

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
        Type type = Nested(typeof(LayoutSnapshot), "CommittedMetadata");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo sizeProperty = type.GetProperty("Size", flags)!;
        MethodInfo completeSize = type.GetMethod("CompleteSizeBeforePublish", flags)!;
        MethodInfo markPublished = type.GetMethod("MarkPublished", flags)!;
        object metadata = Construct(type, [new Size(640, 480), 37L, 21]);
        Size finalSize = new(1280, 720);

        Assert.Null(sizeProperty.SetMethod);
        completeSize.Invoke(metadata, [finalSize]);
        Assert.Equal(finalSize, sizeProperty.GetValue(metadata));
        Assert.Equal(37L, Field(metadata, "Revision"));
        Assert.Equal(21, Field(metadata, "MeasuredCount"));

        TargetInvocationException secondCompletion = Assert.Throws<TargetInvocationException>(
            () => completeSize.Invoke(metadata, [new Size(1, 1)]));
        Assert.IsType<InvalidOperationException>(secondCompletion.InnerException);
        Assert.Equal(finalSize, sizeProperty.GetValue(metadata));

        markPublished.Invoke(metadata, null);
        Parallel.For(0, 64, _ => Assert.Equal(finalSize, sizeProperty.GetValue(metadata)));

        TargetInvocationException postPublishCompletion = Assert.Throws<TargetInvocationException>(
            () => completeSize.Invoke(metadata, [new Size(2, 2)]));
        Assert.IsType<InvalidOperationException>(postPublishCompletion.InnerException);
        TargetInvocationException republish = Assert.Throws<TargetInvocationException>(
            () => markPublished.Invoke(metadata, null));
        Assert.IsType<InvalidOperationException>(republish.InnerException);
        Assert.Equal(finalSize, sizeProperty.GetValue(metadata));
    }

    [Fact]
    public void CompactLayoutSnapshotPayloadsPreserveCapturedReferences()
    {
        IReadOnlyList<BlockBox> blocks = Array.Empty<BlockBox>();
        var sourceMap = new MarkdownSourceMap("immutable source");
        using var snapshot = new LayoutSnapshot(blocks, sourceMap, 320, 240);

        Assert.Same(blocks, snapshot.Blocks);
        Assert.Same(blocks, Field(snapshot, "Blocks"));
        Assert.Same(sourceMap, snapshot.SourceMap);
        Assert.Same(sourceMap, Field(snapshot, "SourceMap"));
        Assert.Equal(snapshot.SourceFingerprint,
            Assert.IsType<MarkdownAutomationTextFingerprint>(Field(snapshot, "SourceFingerprint")));
        Assert.Same(snapshot.SemanticDocument, Field(snapshot, "SemanticDocument"));
    }

    [Fact]
    public void CompactPrivateRangeAndRetirementPayloadsPreserveConstructorValues()
    {
        object range = Construct(Nested(typeof(RasterImageResourceBudget), "IconPayloadRange"), [31, 17]);
        Assert.Equal(31, Field(range, "Offset"));
        Assert.Equal(17, Field(range, "Length"));
        Assert.Equal(48, range.GetType().GetProperty("End")!.GetValue(range));

        var failure = new InvalidOperationException("retained failure");
        object retirement = Construct(Nested(typeof(SnapshotRetirementTracker), "RetirementFailure"), [42L, failure]);
        Assert.Equal(42L, Field(retirement, "Sequence"));
        Assert.Same(failure, Field(retirement, "Exception"));

        Exception[] failures = [failure];
        object cursor = Construct(Nested(typeof(BlockBoxCleanupScope), "AggregateFailureCursor"), [failures, 3, 5]);
        Assert.Same(failures, Field(cursor, "InnerExceptions"));
        Assert.Equal(3, Field(cursor, "NextIndex"));
        Assert.Equal(5, Field(cursor, "Depth"));

        int[] insertions = [5, 12];
        int[] maskedSeparators = [8];
        Type preparedSourceType = Nested(typeof(Gfm029Compatibility), "PreparedSource");
        object preparedSource = Construct(preparedSourceType,
            ["rewritten source", insertions, maskedSeparators]);
        Assert.Equal("rewritten source", Field(preparedSource, "Text"));
        Assert.Equal(true, Field(preparedSource, "HasChanges"));
        Assert.Same(insertions, Field(preparedSource, "_insertions"));
        Assert.Same(maskedSeparators, Field(preparedSource, "_maskedSeparators"));
        Assert.Equal(false, Field(Construct(preparedSourceType,
            ["unchanged source", Array.Empty<int>(), Array.Empty<int>()]), "HasChanges"));
        Assert.Equal(true, Field(Construct(preparedSourceType,
            ["inserted source", insertions, Array.Empty<int>()]), "HasChanges"));
        Assert.Equal(true, Field(Construct(preparedSourceType,
            ["masked source", Array.Empty<int>(), maskedSeparators]), "HasChanges"));

        object lineInfo = Construct(Nested(typeof(Gfm029Compatibility), "LineInfo"), [27, 14]);
        Assert.Equal(27, Field(lineInfo, "Start"));
        Assert.Equal(14, Field(lineInfo, "Length"));

        object dispatchState = Construct(typeof(MarkdownRebuildDispatchState),
            [91L, MarkdownRebuildDispatchPriority.Normal]);
        Assert.Equal(91L, Field(dispatchState, "Ticket"));
        Assert.Equal(MarkdownRebuildDispatchPriority.Normal, Field(dispatchState, "Priority"));

        object command = Construct(typeof(MarkdownTargetCommand),
            [MarkdownCommandKind.Activate, new SourceSpan(6, 11), "target", "cs"]);
        Assert.Equal(MarkdownCommandKind.Activate, Field(command, "Kind"));
        Assert.Equal(new SourceSpan(6, 11), Field(command, "SourceRange"));
        Assert.Equal("target", Field(command, "Target"));
        Assert.Equal("cs", Field(command, "Language"));

        object decision = Construct(typeof(MarkdownLinkActivationDecision),
            [MarkdownLinkInputKind.Touch, MarkdownLinkActivationPolicyResult.DisabledCommand]);
        Assert.Equal(MarkdownLinkInputKind.Touch, Field(decision, "InputKind"));
        Assert.Equal(MarkdownLinkActivationPolicyResult.DisabledCommand, Field(decision, "Result"));
        Assert.True((bool)decision.GetType()
            .GetProperty("IsHandled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(decision)!);

        object clipboard = Construct(typeof(MarkdownTableClipboardPayload), ["cell text", "<table></table>"]);
        Assert.Equal("cell text", Field(clipboard, "Text"));
        Assert.Equal("<table></table>", Field(clipboard, "Html"));

        object fingerprint = Construct(typeof(MarkdownAutomationTextFingerprint), [23, 1UL, 2UL, 3UL, 4UL]);
        Assert.Equal(23, Field(fingerprint, "Length"));

        object highContrast = Construct(typeof(MarkdownHighContrastStyleRoles),
            [MarkdownHighContrastColorRole.Hotlight, MarkdownHighContrastColorRole.Window,
                MarkdownHighContrastColorRole.HighlightText, true, false]);
        Assert.Equal(MarkdownHighContrastColorRole.Hotlight, Field(highContrast, "Foreground"));
        Assert.Equal(MarkdownHighContrastColorRole.Window, Field(highContrast, "Background"));
        Assert.Equal(MarkdownHighContrastColorRole.HighlightText, Field(highContrast, "AccentBar"));
        Assert.Equal(true, Field(highContrast, "Underline"));
        Assert.Equal(false, Field(highContrast, "Strikethrough"));

        var safeHtmlAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "safe",
        };
        object safeHtmlTag = Construct(typeof(SafeHtmlTag),
            [SafeHtmlTagKind.SelfClosing, "img", safeHtmlAttributes, 41, 8]);
        Assert.Equal(SafeHtmlTagKind.SelfClosing, Field(safeHtmlTag, "Kind"));
        Assert.Equal("img", Field(safeHtmlTag, "Name"));
        Assert.Same(safeHtmlAttributes, Field(safeHtmlTag, "Attributes"));
        Assert.Equal(41, Field(safeHtmlTag, "SourceStart"));
        Assert.Equal(8, Field(safeHtmlTag, "SourceLength"));

        var summarized = new MarkdownLayoutRetirementFailureSummary(failure);
        Assert.Equal(failure.GetType().FullName, Field(summarized, "ExceptionType"));
        Assert.Equal(failure.HResult, Field(summarized, "HResultCode"));
        Assert.Null(Field(summarized, "OmittedFailureCount"));
        var omitted = new MarkdownLayoutRetirementFailureSummary(4);
        Assert.Null(Field(omitted, "ExceptionType"));
        Assert.Null(Field(omitted, "HResultCode"));
        Assert.Equal(4, Field(omitted, "OmittedFailureCount"));
    }

    [Fact]
    public void CompactImmutablePayloadsPreserveValuesAndBehavior()
    {
        MarkdownNodeRenderer synchronous = static (_, _) => { };
        MarkdownAsyncNodeRenderer asynchronous = static (_, _) => ValueTask.CompletedTask;
        var factory = new MarkdownOwnedExtensionFactory("owned", static () => null!);

        MarkdownExtensionRegistration extension = MarkdownExtensionRegistration.Extension("gfm");
        Assert.Equal(MarkdownExtensionRegistrationKind.Extension, Field(extension, "Kind"));
        Assert.Equal("gfm", Field(extension, "Identifier"));
        Assert.Null(Field(extension, "SynchronousRenderer"));
        Assert.Null(Field(extension, "AsynchronousRenderer"));
        Assert.Null(Field(extension, "OwnedFactory"));

        MarkdownExtensionRegistration sync = MarkdownExtensionRegistration.SynchronousInline(
            "inline", synchronous);
        Assert.Equal(MarkdownExtensionRegistrationKind.SynchronousInline, Field(sync, "Kind"));
        Assert.Equal("inline", Field(sync, "Identifier"));
        Assert.Same(synchronous, Field(sync, "SynchronousRenderer"));

        MarkdownExtensionRegistration asynchronousRegistration = MarkdownExtensionRegistration.AsynchronousBlock(
            "async", asynchronous);
        Assert.Equal(MarkdownExtensionRegistrationKind.AsynchronousBlock,
            Field(asynchronousRegistration, "Kind"));
        Assert.Equal("async", Field(asynchronousRegistration, "Identifier"));
        Assert.Same(asynchronous, Field(asynchronousRegistration, "AsynchronousRenderer"));

        MarkdownExtensionRegistration owned = MarkdownExtensionRegistration.Owned(factory);
        Assert.Equal(MarkdownExtensionRegistrationKind.OwnedFactory, Field(owned, "Kind"));
        Assert.Same(factory, Field(owned, "OwnedFactory"));

        MarkdownSemanticDocument document = MarkdownSemanticDocument.Empty;
        Assert.Same(document.Root, Field(document, "Root"));
        Assert.Equal(document.Text, Field(document, "Text"));
        Assert.Equal(document.TextProjectionFingerprint, Field(document, "TextProjectionFingerprint"));
        Assert.Equal(document.SemanticRangeFingerprint, Field(document, "SemanticRangeFingerprint"));
        Assert.Same(document.TextElementBoundaries, Field(document, "TextElementBoundaries"));
        Assert.Same(document.TextSpans, Field(document, "TextSpans"));
        Assert.Equal(MarkdownSemanticRole.Document, Field(document.Root, "Role"));
        Assert.Null(Field(document.Root, "Box"));
        MarkdownTextFormatCache formatCache = MarkdownTextFormatCache.Create(document, theme: null);
        Assert.Same(document, Field(formatCache, "Document"));
        Assert.Null(Field(formatCache, "Theme"));

        var style = new ElementStyle();
        var styleRun = new MarkdownTextStyleRun(2, 9, "Emphasis", true, false, style);
        Assert.Equal("Emphasis", Field(styleRun, "ElementKey"));
        Assert.Equal(true, Field(styleRun, "IsSubscript"));
        Assert.Equal(false, Field(styleRun, "IsSuperscript"));
        Assert.Same(style, Field(styleRun, "Style"));

        var metadata = new InlineEmbedAutomationMetadata(
            "checked", "unchecked", "read-only", "toggle", "task-item", isChecked: false,
            canSetState: static _ => true,
            trySetState: static _ => true);
        Assert.Equal("checked", Field(metadata, "CheckedName"));
        Assert.Equal("unchecked", Field(metadata, "UncheckedName"));
        Assert.Equal("read-only", Field(metadata, "ReadOnlyHelpText"));
        Assert.Equal("toggle", Field(metadata, "ToggleHelpText"));
        Assert.Equal("task-item", Field(metadata, "AutomationId"));
        Assert.Equal("unchecked", metadata.CurrentName);
        Assert.Equal("toggle", metadata.CurrentHelpText);
        Assert.True(metadata.TrySetState(true));
        Assert.Equal("checked", metadata.CurrentName);

        var policy = new SafeHtmlRenderPolicy(
            enableLinks: true,
            enableImages: false,
            renderUnknownElementsLiterally: true,
            allowedStyleClasses: ["safe-class"]);
        Assert.Equal(true, Field(policy, "EnableLinks"));
        Assert.Equal(false, Field(policy, "EnableImages"));
        Assert.Equal(true, Field(policy, "RenderUnknownElementsLiterally"));
        SafeHtmlParseLimits actualLimits = Assert.IsType<SafeHtmlParseLimits>(Field(policy, "Limits"));
        Assert.Equal(SafeHtmlParseLimits.Default, actualLimits);
        Assert.True(policy.IsStyleClassAllowed("safe-class"));
        Assert.False(policy.IsStyleClassAllowed("other-class"));

        Func<Microsoft.UI.Xaml.FrameworkElement> elementFactory = static () => null!;
        var inlineRun = new InlineEmbedRun(24, 18, elementFactory);
        Assert.Equal(24f, Field(inlineRun, "DesiredWidth"));
        Assert.Equal(18f, Field(inlineRun, "DesiredHeight"));
        Assert.Same(elementFactory, Field(inlineRun, "ElementFactory"));

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

        Assert.Same(fragment, Field(fallbackKey, "Fragment"));
        Assert.Equal(fallbackKey, new HostedElementFallbackKey(fragment));
        Assert.Equal("sample.factory", Field(box, "FactoryKey"));
        Assert.Equal(new SourceSpan(4, 12), Field(box, "SourceRange"));
        Assert.Same(content.Attributes, Field(box, "Attributes"));
        Assert.Equal(MarkdownAccessibilityRole.Group, Field(box, "AccessibilityRole"));
        Assert.Equal("Sample widget", Field(box, "AccessibilityName"));
        Assert.Equal("Widget description", Field(box, "AccessibilityDescription"));
        Assert.Equal("Widget semantic text", Field(box, "SemanticText"));
        Assert.Equal(fallbackKey, (HostedElementFallbackKey)Field(box, "FallbackKey")!);
        Assert.Equal(43d, box.Measure(200) - box.Margin.Top - box.Margin.Bottom);

    }

    [Fact]
    public void CompactRendererSnapshotPayloadsPreserveConstructorValues()
    {
        MarkdownLayoutContext context = CreateContext();
        Assert.Same(context.ResourceCreator, Field(context, "ResourceCreator"));
        Assert.Same(context.ThemeSnapshot, Field(context, "ThemeSnapshot"));
        Assert.Same(context.SourceMap, Field(context, "SourceMap"));
        Assert.Same(context.Registry, Field(context, "Registry"));
        Assert.Equal(FlowDirection.LeftToRight, Field(context, "FlowDirection"));
        Assert.Equal(context.Language, Field(context, "Language"));
        Assert.Same(context.Culture, Field(context, "Culture"));
        Assert.Null(Field(context, "Dispatcher"));

        string[] aliases = ["safe-class", "compact"];
        var scriptToken = new SafeHtmlInlineState.ScriptStyleAncestryToken("subscript");
        object inlineScope = Construct(Nested(typeof(SafeHtmlInlineState), "Scope"),
            ["a", "Link", "https://example.test", "safe link", true, aliases, scriptToken]);
        Assert.Equal("a", Field(inlineScope, "Name"));
        Assert.Equal("Link", Field(inlineScope, "StyleKey"));
        Assert.Equal("https://example.test", Field(inlineScope, "LinkUrl"));
        Assert.Equal("safe link", Field(inlineScope, "LinkTitle"));
        Assert.Equal(true, Field(inlineScope, "Suppressed"));
        Assert.Same(aliases, Field(inlineScope, "StyleAliases"));
        Assert.Same(scriptToken, Field(inlineScope, "ScriptToken"));

        object blockScope = Construct(Nested(typeof(SafeHtmlBlockScopeTracker), "Scope"),
            ["details", SafeHtmlAlignment.Center, true, 19]);
        Assert.Equal("details", Field(blockScope, "Name"));
        Assert.Equal(SafeHtmlAlignment.Center, Field(blockScope, "Alignment"));
        Assert.Equal(true, Field(blockScope, "SuppressContent"));
        Assert.Equal(19, Field(blockScope, "InlineFlowScopeId"));

        var viewportRange = new ViewportRange(3, 8);
        Assert.Equal(3, Field(viewportRange, "Start"));
        Assert.Equal(8, Field(viewportRange, "End"));
        var lazyBand = new LazyLayoutBand(24.5, 97.25);
        Assert.Equal(24.5, Field(lazyBand, "Top"));
        Assert.Equal(97.25, Field(lazyBand, "Bottom"));
        var transform = new VectorViewportTransform(1.5f, 12f, -4f);
        Assert.Equal(1.5f, Field(transform, "Scale"));
        Assert.Equal(12f, Field(transform, "TranslationX"));
        Assert.Equal(-4f, Field(transform, "TranslationY"));

        Color primary = Color.FromArgb(255, 12, 34, 56);
        Color secondary = Color.FromArgb(255, 21, 43, 65);
        Color accent = Color.FromArgb(255, 32, 54, 76);
        Color surface = Color.FromArgb(255, 43, 65, 87);
        var overrides = new ThemeResolver.PlatformThemeColorOverrides(primary, secondary, accent, surface);
        Assert.Equal(primary, Field(overrides, "PrimaryText"));
        Assert.Equal(secondary, Field(overrides, "SecondaryText"));
        Assert.Equal(accent, Field(overrides, "AccentText"));
        Assert.Equal(surface, Field(overrides, "Surface"));
        var colors = new ThemeResolver.PlatformThemeColors(primary, secondary, accent, surface);
        Assert.Equal(primary, Field(colors, "PrimaryText"));
        Assert.Equal(secondary, Field(colors, "SecondaryText"));
        Assert.Equal(accent, Field(colors, "AccentText"));
        Assert.Equal(surface, Field(colors, "Surface"));

        string parsedSource = new("parsed source".ToCharArray());
        var parsedAst = new Markdig.Syntax.MarkdownDocument();
        var parsedMarkdown = new ParsedMarkdown(parsedSource, parsedAst);
        Assert.Same(parsedSource, parsedMarkdown.SourceText);
        Assert.Same(parsedSource, Field(parsedMarkdown, "SourceText"));
        Assert.Same(parsedAst, parsedMarkdown.Document);
        Assert.Same(parsedAst, Field(parsedMarkdown, "Document"));

        var parsedBlock = new ParagraphBlock(null!);
        var factory = new TestEmbedFactory();
        var embedBox = new EmbedBox(parsedBlock, factory);
        Assert.Same(parsedBlock, Field(embedBox, "SourceBlock"));
        Assert.Same(factory, Field(embedBox, "Factory"));

        var stringProvider = new TestStringProvider();
        MarkdownLocalizationSnapshot localization = MarkdownLocalizationSnapshot.Capture(
            "fr-FR", environmentLanguage: null, stringProvider, CultureInfo.InvariantCulture)!;
        Assert.Equal("fr-FR", Assert.IsType<CultureInfo>(Field(localization, "Culture")).Name);
        Assert.Same(stringProvider, Field(localization, "StringProvider"));

        var request = new object();
        using (var operation = new HostedElementRealizationGate<object>.Operation(request, default))
            Assert.Same(request, Field(operation, "Request"));

        var result = new CodeBlockHighlightResult(spans: null);
        var cache = new BoundedCodeBlockHighlightCache<string>(capacity: 2);
        cache.Set("payload-key", result);
        object recency = Field(cache, "_recency")!;
        object node = recency.GetType().GetProperty("First")!.GetValue(recency)!;
        object entry = node.GetType().GetProperty("Value")!.GetValue(node)!;
        Assert.Equal("payload-key", Field(entry, "Key"));
        Assert.Same(result, Field(entry, "Result"));
        Assert.Equal(128L, Field(entry, "Weight"));
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            ["sample-hosted"] = style,
        };
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
            new MarkdownSourceMap("Hosted element source"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static Type Nested(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.NonPublic) ?? throw new InvalidOperationException(name);

    private static void AssertReadonly(Type type, string names)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.False(type.IsVisible); // None of these carriers is a public API.
        foreach (string name in names.Split(' '))
        {
            FieldInfo field = type.GetField(name, flags)!;
            Assert.NotNull(field);
            Assert.True(field.IsInitOnly, $"{type.Name}.{name}");
            Assert.Null(type.GetProperty(name, flags));
        }
    }

    private static object Construct(Type type, object[] values) =>
        Assert.Single(Array.FindAll(
            type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Length == values.Length)).Invoke(values);

    private static CanvasBitmap CreateBitmap(CanvasDevice device) => CanvasBitmap.CreateFromBytes(
        device,
        new byte[4],
        1,
        1,
        DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private static object? Field(object payload, string name) =>
        payload.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(payload);

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
