using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveCompletedSceneCacheTests
{
    [Fact]
    public void Cache_EnforcesWeightedLruAndUsesOwnerReferenceIdentity()
    {
        using var cache = new MarkdownCompletedSceneCache(576);
        var firstOwner = new EqualOwner();
        var secondOwner = new EqualOwner();
        MarkdownDeferredSceneResult first = CreateResult("first");
        MarkdownDeferredSceneResult second = CreateResult("second");
        MarkdownDeferredSceneResult third = CreateResult("third");

        Assert.True(cache.Retain(firstOwner, "marker", first, estimatedWeightBytes: 100));
        Assert.True(cache.Retain(secondOwner, "marker", second, estimatedWeightBytes: 100));
        Assert.True(cache.TryGetResult(firstOwner, "marker", out MarkdownDeferredSceneResult? hit));
        Assert.Same(first, hit);

        Assert.True(cache.Retain(firstOwner, "next", third, estimatedWeightBytes: 100));

        Assert.True(cache.TryGetResult(firstOwner, "marker", out hit));
        Assert.Same(first, hit);
        Assert.True(cache.TryGetResult(firstOwner, "next", out hit));
        Assert.Same(third, hit);
        Assert.False(cache.TryGetResult(secondOwner, "marker", out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(520, cache.RetainedWeightBytes);
        Assert.Equal(3, cache.Hits);
        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public void Cache_RejectsZeroBudgetAndOversizedResults()
    {
        var owner = new object();
        MarkdownDeferredSceneResult result = CreateResult("oversized");
        using var disabled = new MarkdownCompletedSceneCache(0);
        Assert.False(disabled.Retain(owner, "zero", result, estimatedWeightBytes: 1));
        Assert.True(disabled.RetainMetadata(owner, "zero", new object(), estimatedWeightBytes: 1));
        Assert.Equal(0, disabled.Count);
        Assert.Equal(0, disabled.RetainedWeightBytes);
        Assert.Equal(1, disabled.MetadataCount);

        using var bounded = new MarkdownCompletedSceneCache(256);
        Assert.False(bounded.Retain(owner, "large", result, estimatedWeightBytes: 161));
        Assert.Equal(0, bounded.Count);
        Assert.Equal(0, bounded.RetainedWeightBytes);
    }

    [Fact]
    public void Cache_RemoveOwnerAndTrimDropOnlyStrongCacheEntries()
    {
        using var cache = new MarkdownCompletedSceneCache(2048);
        var firstOwner = new object();
        var secondOwner = new object();
        Assert.True(cache.Retain(firstOwner, "a", CreateResult("a"), estimatedWeightBytes: 200));
        Assert.True(cache.Retain(firstOwner, "b", CreateResult("b"), estimatedWeightBytes: 200));
        Assert.True(cache.Retain(secondOwner, "a", CreateResult("c"), estimatedWeightBytes: 200));
        Assert.True(cache.RetainMetadata(firstOwner, "metadata", new object(), estimatedWeightBytes: 300));
        Assert.True(cache.RetainMetadata(secondOwner, "metadata", new object(), estimatedWeightBytes: 300));

        cache.RemoveOwner(firstOwner);

        Assert.False(cache.TryGetResult(firstOwner, "a", out _));
        Assert.False(cache.TryGetResult(firstOwner, "b", out _));
        Assert.True(cache.TryGetResult(secondOwner, "a", out _));
        Assert.False(cache.TryGetMetadata(firstOwner, "metadata", out _));
        Assert.True(cache.TryGetMetadata(secondOwner, "metadata", out _));
        Assert.Equal(1, cache.Count);
        Assert.Equal(360, cache.RetainedWeightBytes);
        Assert.Equal(1, cache.MetadataCount);
        Assert.Equal(460, cache.MetadataRetainedWeightBytes);

        cache.Trim();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedWeightBytes);
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(0, cache.MetadataCount);
        Assert.Equal(0, cache.MetadataRetainedWeightBytes);
        Assert.Equal(1, cache.MetadataEvictions);
    }

    [Fact]
    public void Cache_MetadataLruIsAggregateAcrossMarkersAndDocumentOwners()
    {
        using var cache = new MarkdownCompletedSceneCache(0);
        object ownerA = new();
        object ownerB = new();
        object ownerC = new();
        object a = new();
        object b = new();
        object c = new();
        object d = new();
        object e = new();
        const long graphWeight = 1_000_000;

        Assert.True(cache.RetainMetadata(ownerA, "a", a, graphWeight));
        Assert.True(cache.RetainMetadata(ownerB, "b", b, graphWeight));
        Assert.True(cache.RetainMetadata(ownerA, "c", c, graphWeight));
        Assert.True(cache.RetainMetadata(ownerB, "d", d, graphWeight));
        Assert.True(cache.TryGetMetadata(ownerA, "a", out object? promoted));
        Assert.Same(a, promoted);
        Assert.True(cache.RetainMetadata(ownerC, "e", e, graphWeight));

        Assert.True(cache.MetadataRetainedWeightBytes <= cache.MetadataBudgetBytes);
        Assert.Equal(4, cache.MetadataCount);
        Assert.Equal(1, cache.MetadataEvictions);
        Assert.Equal(1, cache.MetadataHits);
        Assert.False(cache.TryGetMetadata(ownerB, "b", out _));
        Assert.True(cache.TryGetMetadata(ownerA, "a", out promoted));
        Assert.Same(a, promoted);
        Assert.True(cache.TryGetMetadata(ownerA, "c", out promoted));
        Assert.Same(c, promoted);
        Assert.True(cache.TryGetMetadata(ownerB, "d", out promoted));
        Assert.Same(d, promoted);
        Assert.True(cache.TryGetMetadata(ownerC, "e", out promoted));
        Assert.Same(e, promoted);
        Assert.False(cache.RetainMetadata(ownerC, "too-large", new object(), cache.MetadataBudgetBytes));
        Assert.Equal(4, cache.MetadataCount);
    }

    [Fact]
    public void Cache_LoweringBudgetEvictsLeastRecentUntilWithinLimit()
    {
        using var cache = new MarkdownCompletedSceneCache(1024);
        var owner = new object();
        Assert.True(cache.Retain(owner, "old", CreateResult("old"), estimatedWeightBytes: 200));
        Assert.True(cache.Retain(owner, "new", CreateResult("new"), estimatedWeightBytes: 200));
        Assert.True(cache.TryGetResult(owner, "old", out _));

        cache.SetBudget(360);

        Assert.True(cache.TryGetResult(owner, "old", out _));
        Assert.False(cache.TryGetResult(owner, "new", out _));
        Assert.Equal(360, cache.RetainedWeightBytes);
        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public async Task Cache_DisposeRacingLookupsAndAdmissionReturnsMissInsteadOfThrowing()
    {
        var cache = new MarkdownCompletedSceneCache(4096);
        object owner = new();
        MarkdownDeferredSceneResult result = CreateResult("race");
        Assert.True(cache.Retain(owner, "marker", result, estimatedWeightBytes: 128));
        object metadata = new();
        Assert.True(cache.RetainMetadata(owner, "marker", metadata, estimatedWeightBytes: 128));
        using var start = new ManualResetEventSlim();

        Task racingLookups = Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < 256; index++)
            {
                cache.TryGetResult(owner, "marker", out _);
                cache.Retain(owner, "marker", result, estimatedWeightBytes: 128);
                cache.TryGetMetadata(owner, "marker", out _);
                cache.RetainMetadata(owner, "marker", metadata, estimatedWeightBytes: 128);
            }
        });

        start.Set();
        cache.Dispose();
        await racingLookups.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(cache.TryGetResult(owner, "marker", out MarkdownDeferredSceneResult? hit));
        Assert.Null(hit);
        Assert.False(cache.Retain(owner, "marker", result, estimatedWeightBytes: 128));
        Assert.False(cache.TryGetMetadata(owner, "marker", out object? metadataHit));
        Assert.Null(metadataHit);
        Assert.False(cache.RetainMetadata(owner, "marker", metadata, estimatedWeightBytes: 128));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedWeightBytes);
        Assert.Equal(0, cache.MetadataCount);
        Assert.Equal(0, cache.MetadataRetainedWeightBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_TrimOrLastOwnerRemovalAndDisposeReplaceHighWaterCollections(bool retireOwner)
    {
        var cache = new MarkdownCompletedSceneCache(2 * 1024 * 1024);
        object owner = new();
        MarkdownDeferredSceneResult result = CreateResult("capacity");
        for (int index = 0; index < 4096; index++)
        {
            string marker = $"scene-{index}";
            if (!cache.Retain(owner, marker, result, estimatedWeightBytes: 1) ||
                !cache.RetainMetadata(owner, marker, new object(), estimatedWeightBytes: 1))
            {
                throw new InvalidOperationException("The capacity fixture should fit both cache budgets.");
            }
        }

        object graphEntriesBeforeTrim = GetCacheCollection(cache, "_entries");
        object graphRecencyBeforeTrim = GetCacheCollection(cache, "_recency");
        object metadataEntriesBeforeTrim = GetCacheCollection(cache, "_metadataEntries");
        object metadataRecencyBeforeTrim = GetCacheCollection(cache, "_metadataRecency");
        Assert.True(cache.Count > 1_000);
        Assert.True(cache.MetadataCount > 1_000);

        if (retireOwner)
            cache.RemoveOwner(owner);
        else
            cache.Trim();

        Assert.NotSame(graphEntriesBeforeTrim, GetCacheCollection(cache, "_entries"));
        Assert.NotSame(graphRecencyBeforeTrim, GetCacheCollection(cache, "_recency"));
        Assert.NotSame(metadataEntriesBeforeTrim, GetCacheCollection(cache, "_metadataEntries"));
        Assert.NotSame(metadataRecencyBeforeTrim, GetCacheCollection(cache, "_metadataRecency"));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.MetadataCount);
        Assert.Equal(0, cache.RetainedWeightBytes);
        Assert.Equal(0, cache.MetadataRetainedWeightBytes);

        Assert.True(cache.Retain(owner, "after-trim", result, estimatedWeightBytes: 1));
        Assert.True(cache.RetainMetadata(owner, "after-trim", new object(), estimatedWeightBytes: 1));
        object graphEntriesBeforeDispose = GetCacheCollection(cache, "_entries");
        object metadataEntriesBeforeDispose = GetCacheCollection(cache, "_metadataEntries");

        cache.Dispose();

        Assert.NotSame(graphEntriesBeforeDispose, GetCacheCollection(cache, "_entries"));
        Assert.NotSame(metadataEntriesBeforeDispose, GetCacheCollection(cache, "_metadataEntries"));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.MetadataCount);
        Assert.Equal(0, cache.RetainedWeightBytes);
        Assert.Equal(0, cache.MetadataRetainedWeightBytes);
    }

    [Fact]
    public void Descriptor_ReconstructsWholeContentTreeAroundLiveSceneAfterOriginalDies()
    {
        (MarkdownCompletedSceneDescriptor descriptor,
            object metadataOwner,
            MarkdownVectorScene scene,
            WeakReference<MarkdownDeferredSceneResult> original,
            WeakReference<MarkdownContent> originalRoot) = CreateMultiItemDescriptor();

        ForceCollection();

        Assert.False(original.TryGetTarget(out _));
        Assert.False(originalRoot.TryGetTarget(out _));
        Assert.True(descriptor.EstimatedGraphWeightBytes > 0);
        Assert.True(descriptor.MetadataWeightBytes > 0);
        Assert.True(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Fragment.Items.Count);
        Assert.Equal("lead", restored.Fragment.Items[0].Text);
        Assert.Equal(MarkdownContentKind.Container, restored.Fragment.Items[1].Kind);
        Assert.Equal("group-name", restored.Fragment.Items[1].AccessibilityName);
        Assert.Equal("value", restored.Fragment.Items[1].Attributes["sample"]);
        Assert.Equal(2, restored.Fragment.Items[1].Children.Count);
        Assert.Same(scene, restored.Fragment.Items[1].Children[0].VectorScene);
        Assert.Equal("nested sibling", restored.Fragment.Items[1].Children[1].Text);
        Assert.Equal("tail", restored.Fragment.Items[2].Text);
        Assert.Single(restored.Diagnostics);
        Assert.Equal("TEST001", restored.Diagnostics[0].Code);
        GC.KeepAlive(metadataOwner);
        GC.KeepAlive(scene);
    }

    [Fact]
    public void Descriptor_RehydratesInlineLikeSceneHeldOnlyByExternalRunOwner()
    {
        (MarkdownCompletedSceneDescriptor descriptor,
            object metadataOwner,
            MarkdownVectorScene scene,
            WeakReference<MarkdownDeferredSceneResult> original,
            WeakReference<MarkdownContent> originalNode) = CreateInlineLikeDescriptor();

        ForceCollection();

        Assert.False(original.TryGetTarget(out _));
        Assert.False(originalNode.TryGetTarget(out _));
        Assert.True(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        MarkdownContent item = Assert.Single(restored!.Fragment.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, item.Kind);
        Assert.Equal(MarkdownAccessibilityRole.Math, item.AccessibilityRole);
        Assert.Equal("x squared", item.SemanticText);
        Assert.Same(scene, item.VectorScene);
        GC.KeepAlive(metadataOwner);
        GC.KeepAlive(scene);
    }

    [Fact]
    public void Descriptor_FailsClosedWhenARequiredSceneIsNoLongerLive()
    {
        MarkdownCompletedSceneDescriptor descriptor = CreateCollectibleSceneDescriptor(
            out WeakReference<MarkdownVectorScene> sceneReference,
            out object metadataOwner);
        ForceCollection();

        Assert.False(sceneReference.TryGetTarget(out _));
        Assert.False(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        Assert.Null(restored);
        GC.KeepAlive(metadataOwner);
    }

    [Fact]
    public void Descriptor_MetadataAboveOneMiBFallsBackToWeakResultThenNormalMiss()
    {
        MarkdownCompletedSceneDescriptor descriptor = CreateOversizedMetadataDescriptor(
            out WeakReference<MarkdownDeferredSceneResult> resultReference);

        Assert.Equal(0, descriptor.MetadataWeightBytes);
        ForceCollection();

        Assert.False(resultReference.TryGetTarget(out _));
        Assert.False(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        Assert.Null(restored);
    }

    [Fact]
    public void Descriptor_OverBudgetStillReusesTheLiveResult()
    {
        MarkdownDeferredSceneResult result = CreateOversizedResult();
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);

        Assert.Equal(0, descriptor.MetadataWeightBytes);
        Assert.True(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        Assert.Same(result, restored);
    }

    [Fact]
    public void Descriptor_CreateAndRestoreHonorCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        MarkdownDeferredSceneResult result = CreateResult("cancelled");

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownCompletedSceneDescriptor.Create(result, source.Token));

        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        Assert.Throws<OperationCanceledException>(() => descriptor.TryRestore(out _, source.Token));
    }

    [Fact]
    public void Descriptor_MetadataExpiresAfterTrimWhenNoLeaseRemains()
    {
        (MarkdownCompletedSceneDescriptor descriptor,
            MarkdownCompletedSceneCache cache,
            object owner,
            string marker,
            WeakReference<MarkdownDeferredSceneResult> resultReference,
            WeakReference<object> metadataReference) = CreateCachedMetadataDescriptor();

        Assert.True(cache.MetadataCount > 0);
        cache.Trim();
        ForceCollection();

        Assert.False(resultReference.TryGetTarget(out _));
        Assert.False(metadataReference.TryGetTarget(out _));
        Assert.False(cache.TryGetMetadata(owner, marker, out _));
        Assert.False(descriptor.TryRestore(out MarkdownDeferredSceneResult? restored));
        Assert.Null(restored);
        cache.Dispose();
    }

    [Fact]
    public void Descriptor_SharesOneDiagnosticContributionAndSurvivesCacheTrim()
    {
        (MarkdownCompletedSceneDescriptor descriptor,
            MarkdownCompletedSceneCache cache,
            WeakReference<MarkdownDeferredSceneResult> resultReference) = CreateDiagnosticDescriptor();

        // The reporting view and the immutable plan contribution must be the
        // same backing ImmutableArray, not a second per-marker reference array.
        Assert.True(descriptor.DiagnosticContribution.Equals((object)descriptor.Diagnostics));
        Assert.Single(descriptor.DiagnosticContribution);
        Assert.Equal("TEST001", descriptor.DiagnosticContribution[0].Code);

        cache.Trim();
        ForceCollection();

        Assert.False(resultReference.TryGetTarget(out _));
        Assert.Equal("test diagnostic", descriptor.Diagnostics[0].Message);
        cache.Dispose();
    }

    [Fact]
    public void Descriptor_GraphWeightIncludesDiagnosticReferenceArray()
    {
        MarkdownDeferredSceneResult withDiagnostics = CreateResult("weight");
        var withoutDiagnostics = new MarkdownDeferredSceneResult(
            withDiagnostics.Fragment,
            Array.Empty<MarkdownDiagnostic>());

        long withWeight = MarkdownCompletedSceneDescriptor.Create(withDiagnostics).EstimatedGraphWeightBytes;
        long withoutWeight = MarkdownCompletedSceneDescriptor.Create(withoutDiagnostics).EstimatedGraphWeightBytes;

        Assert.True(withWeight - withoutWeight >= 24 + IntPtr.Size);
    }

    [Fact]
    public void Descriptor_EstimatorCountsSharedSceneGraphOncePerFragment()
    {
        MarkdownVectorScene sharedScene = CreateScene();
        MarkdownDeferredSceneResult one = CreateVectorResult(sharedScene, "one");
        MarkdownDeferredSceneResult two = CreateTwoVectorResult(sharedScene);
        MarkdownDeferredSceneResult duplicated = CreateTwoVectorResult(CreateScene(), CreateScene());

        long oneWeight = MarkdownCompletedSceneDescriptor.Create(one).EstimatedGraphWeightBytes;
        long sharedWeight = MarkdownCompletedSceneDescriptor.Create(two).EstimatedGraphWeightBytes;
        long duplicateWeight = MarkdownCompletedSceneDescriptor.Create(duplicated).EstimatedGraphWeightBytes;

        Assert.True(sharedWeight > oneWeight);
        Assert.True(duplicateWeight > sharedWeight);
    }

    private static MarkdownDeferredSceneResult CreateResult(string text)
    {
        var content = new MarkdownContentBuilder();
        content.AddText(text, new SourceSpan(0, text.Length));
        return new MarkdownDeferredSceneResult(content.Build(),
        [
            new MarkdownDiagnostic(
                "TEST001",
                MarkdownDiagnosticSeverity.Warning,
                "test diagnostic",
                new SourceSpan(0, text.Length)),
        ]);
    }

    private static object GetCacheCollection(MarkdownCompletedSceneCache cache, string name) =>
        typeof(MarkdownCompletedSceneCache)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(cache) ?? throw new InvalidOperationException($"Cache collection '{name}' was not found.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        MarkdownCompletedSceneDescriptor Descriptor,
        object MetadataOwner,
        MarkdownVectorScene Scene,
        WeakReference<MarkdownDeferredSceneResult> Original,
        WeakReference<MarkdownContent> OriginalRoot) CreateMultiItemDescriptor()
    {
        MarkdownVectorScene scene = CreateScene();
        var builder = new MarkdownContentBuilder();
        builder.AddText("lead", new SourceSpan(0, 4));
        builder.AddContainer(
            MarkdownContentKind.Container,
            MarkdownStyleRole.Body,
            new SourceSpan(4, 8),
            MarkdownAccessibilityRole.Group,
            children =>
            {
                children.AddVectorScene(
                    scene,
                    new SourceSpan(5, 4),
                    MarkdownStyleRole.Math,
                    MarkdownAccessibilityRole.Math,
                    accessibilityName: "equation",
                    accessibilityDescription: "formula description",
                    semanticText: "a+b");
                children.AddText("nested sibling", new SourceSpan(9, 5));
            },
            attributes: new Dictionary<string, string> { ["sample"] = "value" },
            accessibilityName: "group-name",
            accessibilityDescription: "group-description");
        builder.AddText("tail", new SourceSpan(12, 4));

        var result = new MarkdownDeferredSceneResult(builder.Build(),
        [
            new MarkdownDiagnostic(
                "TEST001",
                MarkdownDiagnosticSeverity.Warning,
                "descriptor diagnostic",
                new SourceSpan(4, 8)),
        ]);
        MarkdownContent root = result.Fragment.Items[1];
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        object metadataOwner = descriptor.DetachMetadataOwner() ??
            throw new InvalidOperationException("The test scene should have reconstruction metadata.");
        return (
            descriptor,
            metadataOwner,
            scene,
            new WeakReference<MarkdownDeferredSceneResult>(result),
            new WeakReference<MarkdownContent>(root));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        MarkdownCompletedSceneDescriptor Descriptor,
        object MetadataOwner,
        MarkdownVectorScene Scene,
        WeakReference<MarkdownDeferredSceneResult> Original,
        WeakReference<MarkdownContent> OriginalNode) CreateInlineLikeDescriptor()
    {
        MarkdownVectorScene scene = CreateScene();
        MarkdownDeferredSceneResult result = CreateVectorResult(scene, "x squared");
        MarkdownContent node = Assert.Single(result.Fragment.Items);
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        object metadataOwner = descriptor.DetachMetadataOwner() ??
            throw new InvalidOperationException("The test scene should have reconstruction metadata.");
        return (
            descriptor,
            metadataOwner,
            scene,
            new WeakReference<MarkdownDeferredSceneResult>(result),
            new WeakReference<MarkdownContent>(node));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MarkdownCompletedSceneDescriptor CreateCollectibleSceneDescriptor(
        out WeakReference<MarkdownVectorScene> sceneReference,
        out object metadataOwner)
    {
        MarkdownVectorScene scene = CreateScene();
        MarkdownDeferredSceneResult result = CreateVectorResult(scene, "temporary scene");
        sceneReference = new WeakReference<MarkdownVectorScene>(scene);
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        metadataOwner = descriptor.DetachMetadataOwner() ??
            throw new InvalidOperationException("The test scene should have reconstruction metadata.");
        return descriptor;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MarkdownCompletedSceneDescriptor CreateOversizedMetadataDescriptor(
        out WeakReference<MarkdownDeferredSceneResult> resultReference)
    {
        MarkdownDeferredSceneResult result = CreateOversizedResult();
        resultReference = new WeakReference<MarkdownDeferredSceneResult>(result);
        return MarkdownCompletedSceneDescriptor.Create(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        MarkdownCompletedSceneDescriptor Descriptor,
        MarkdownCompletedSceneCache Cache,
        object Owner,
        string Marker,
        WeakReference<MarkdownDeferredSceneResult> Result,
        WeakReference<object> Metadata) CreateCachedMetadataDescriptor()
    {
        var cache = new MarkdownCompletedSceneCache(0);
        object owner = new();
        const string marker = "metadata-expiry";
        MarkdownVectorScene scene = CreateScene();
        MarkdownDeferredSceneResult result = CreateVectorResult(scene, "metadata");
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        object metadata = descriptor.DetachMetadataOwner() ??
            throw new InvalidOperationException("The test scene should have reconstruction metadata.");
        if (!cache.RetainMetadata(owner, marker, metadata, descriptor.MetadataWeightBytes))
            throw new InvalidOperationException("The metadata should fit the fixed cache budget.");
        return (
            descriptor,
            cache,
            owner,
            marker,
            new WeakReference<MarkdownDeferredSceneResult>(result),
            new WeakReference<object>(metadata));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        MarkdownCompletedSceneDescriptor Descriptor,
        MarkdownCompletedSceneCache Cache,
        WeakReference<MarkdownDeferredSceneResult> Result) CreateDiagnosticDescriptor()
    {
        var cache = new MarkdownCompletedSceneCache(4096);
        object owner = new();
        const string marker = "diagnostic-trim";
        MarkdownDeferredSceneResult result = CreateResult("diagnostic retention");
        MarkdownCompletedSceneDescriptor descriptor = MarkdownCompletedSceneDescriptor.Create(result);
        object metadata = descriptor.DetachMetadataOwner() ??
            throw new InvalidOperationException("The test result should have reconstruction metadata.");
        if (!cache.Retain(owner, marker, result, descriptor.EstimatedGraphWeightBytes) ||
            !cache.RetainMetadata(owner, marker, metadata, descriptor.MetadataWeightBytes))
        {
            throw new InvalidOperationException("The test result should fit both cache budgets.");
        }

        return (descriptor, cache, new WeakReference<MarkdownDeferredSceneResult>(result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MarkdownDeferredSceneResult CreateOversizedResult()
    {
        var builder = new MarkdownContentBuilder();
        for (int index = 0; index < 7_000; index++)
            builder.AddText("x", new SourceSpan(index, 1));

        return new MarkdownDeferredSceneResult(builder.Build(), Array.Empty<MarkdownDiagnostic>());
    }

    private static MarkdownDeferredSceneResult CreateVectorResult(
        MarkdownVectorScene scene,
        string semanticText)
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            scene,
            new SourceSpan(0, semanticText.Length),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            accessibilityName: "math name",
            accessibilityDescription: "math help",
            semanticText: semanticText);
        return new MarkdownDeferredSceneResult(content.Build(), Array.Empty<MarkdownDiagnostic>());
    }

    private static MarkdownDeferredSceneResult CreateTwoVectorResult(
        MarkdownVectorScene first,
        MarkdownVectorScene? second = null)
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            first,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            semanticText: "a");
        content.AddVectorScene(
            second ?? first,
            new SourceSpan(1, 1),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            semanticText: "b");
        return new MarkdownDeferredSceneResult(content.Build(), Array.Empty<MarkdownDiagnostic>());
    }

    private static MarkdownVectorScene CreateScene() => new(
        12,
        8,
        6,
        [
            MarkdownVectorCommand.DrawRectangle(
                new MarkdownVectorRectangle(1, 1, 8, 5),
                new MarkdownVectorPaintStyle(fillArgb: 0xff123456)),
        ],
        referenceFontSize: 12);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class EqualOwner
    {
        public override bool Equals(object? obj) => obj is EqualOwner;
        public override int GetHashCode() => 1;
    }
}
