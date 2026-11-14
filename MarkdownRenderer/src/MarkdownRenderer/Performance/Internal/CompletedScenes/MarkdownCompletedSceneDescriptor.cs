using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;

namespace MarkdownRenderer.Performance.Internal.CompletedScenes;

/// <summary>
/// Weakly describes a completed fragment without rooting its vector command
/// graphs. While any live box/run still owns a scene, the fragment can be
/// reconstructed around that same scene instead of recompiling it.
/// </summary>
internal sealed class MarkdownCompletedSceneDescriptor
{
    private const long MaximumReconstructionMetadataBytes = 1024L * 1024;

    private readonly WeakReference<MarkdownDeferredSceneResult> _originalResult;
    private readonly WeakReference<ReconstructionMetadata>? _metadata;
    private ReconstructionMetadata? _pendingMetadata;
    private readonly ImmutableArray<MarkdownDiagnostic> _diagnostics;
    private readonly bool _canReconstruct;

    private MarkdownCompletedSceneDescriptor(
        MarkdownDeferredSceneResult result,
        ReconstructionMetadata? metadata,
        ImmutableArray<MarkdownDiagnostic> diagnostics,
        long estimatedGraphWeightBytes,
        long metadataWeightBytes,
        MarkdownSceneProjectionSignature? singleSceneProjection)
    {
        _originalResult = new WeakReference<MarkdownDeferredSceneResult>(result);
        _metadata = metadata is null ? null : new WeakReference<ReconstructionMetadata>(metadata);
        _pendingMetadata = metadata;
        _diagnostics = diagnostics;
        _canReconstruct = metadata is not null;
        EstimatedGraphWeightBytes = estimatedGraphWeightBytes;
        MetadataWeightBytes = metadataWeightBytes;
        SingleSceneProjection = singleSceneProjection;
        ProjectionIdentity = singleSceneProjection is { } signature ? signature : null;
    }

    /// <summary>Estimated managed graph weight captured once at completion.</summary>
    internal long EstimatedGraphWeightBytes { get; }

    /// <summary>
    /// Approximate reconstruction metadata retained by this descriptor. It
    /// excludes diagnostics maintained separately for plan reporting and is not
    /// part of the completed-result graph-cache budget.
    /// </summary>
    internal long MetadataWeightBytes { get; }

    internal readonly MarkdownSceneProjectionSignature? SingleSceneProjection;
    // One boxed scalar digest is shared by the descriptor and materialized
    // binding; it contains no scene/content/native references.
    internal readonly object? ProjectionIdentity;

    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics => _diagnostics;

    internal ImmutableArray<MarkdownDiagnostic> DiagnosticContribution => _diagnostics;

    internal static MarkdownCompletedSceneDescriptor Create(
        MarkdownDeferredSceneResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Fragment);
        ArgumentNullException.ThrowIfNull(result.Diagnostics);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MarkdownContent> sourceItems = result.Fragment.Items;
        long graphWeight = CompletedSceneGraphWeightEstimator.Estimate(sourceItems, result.Diagnostics, cancellationToken);
        ImmutableArray<MarkdownDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<MarkdownDiagnostic>(result.Diagnostics.Count);
        for (int index = 0; index < result.Diagnostics.Count; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(result.Diagnostics[index]);
        }
        ImmutableArray<MarkdownDiagnostic> diagnosticContribution = diagnostics.MoveToImmutable();

        bool canReconstruct = result.Fragment.DeferredSceneRenderer is null &&
            result.Fragment.DeferredSceneRendererLifetime is null;
        CompletedSceneNodeDescriptor[] descriptors = Array.Empty<CompletedSceneNodeDescriptor>();
        long metadataWeight = 0;
        bool hasReconstructionMetadata = canReconstruct &&
            CompletedSceneNodeDescriptor.TryCreateGraph(
                sourceItems,
                MaximumReconstructionMetadataBytes,
                cancellationToken,
                out descriptors,
                out metadataWeight);

        if (!hasReconstructionMetadata)
        {
            // Descriptors are only a cache optimization. An unusually broad
            // extension result remains publishable and may be retained by the
            // strong weighted cache, but is re-prepared after that cache drops it.
            descriptors = Array.Empty<CompletedSceneNodeDescriptor>();
            metadataWeight = 0;
        }
        ReconstructionMetadata? metadata = hasReconstructionMetadata
            ? new ReconstructionMetadata(descriptors)
            : null;

        return new MarkdownCompletedSceneDescriptor(
            result,
            metadata,
            diagnosticContribution,
            graphWeight,
            metadataWeight,
            sourceItems.Count == 1 && sourceItems[0].Kind == MarkdownContentKind.VectorScene &&
                sourceItems[0].VectorScene is not null && sourceItems[0].Children.Count == 0 &&
                !sourceItems[0].Attributes.ContainsKey(MarkdownContentAttributes.DeferredScene)
                ? MarkdownSceneProjectionSignature.Create(sourceItems[0], cancellationToken)
                : null);
    }

    /// <summary>
    /// Transfers the descriptor's temporary strong construction reference to
    /// the bounded metadata cache before the descriptor is published.
    /// </summary>
    internal object? DetachMetadataOwner() => Interlocked.Exchange(ref _pendingMetadata, null);

    /// <summary>
    /// Reuses the original immutable result if still alive, otherwise rebuilds
    /// its content tree using only live weak scene targets and copied scalars.
    /// </summary>
    internal bool TryGetOriginal(out MarkdownDeferredSceneResult? result) =>
        _originalResult.TryGetTarget(out result);

    internal bool TryRestore(
        out MarkdownDeferredSceneResult? result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetOriginal(out result))
            return true;

        if (!_canReconstruct || _metadata is null ||
            !_metadata.TryGetTarget(out ReconstructionMetadata? metadata))
        {
            result = null;
            return false;
        }

        if (!CompletedSceneNodeDescriptor.TryRestoreGraph(metadata.Items, cancellationToken, out MarkdownContent[] items))
        {
            result = null;
            return false;
        }

        MarkdownContentFragment fragment = items.Length == 0
            ? MarkdownContentFragment.Empty
            : new MarkdownContentFragment(new ReadOnlyCollection<MarkdownContent>(items));
        result = new MarkdownDeferredSceneResult(fragment, _diagnostics);
        return true;
    }

    private sealed class ReconstructionMetadata(CompletedSceneNodeDescriptor[] items)
    {
        internal CompletedSceneNodeDescriptor[] Items { get; } = items;
    }
}
