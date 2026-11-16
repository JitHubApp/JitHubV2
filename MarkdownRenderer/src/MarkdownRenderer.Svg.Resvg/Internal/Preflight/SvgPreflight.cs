using System.Xml;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

internal static class SvgPreflight
{
    internal static readonly string[] ForbiddenElements =
    [
        "script", "foreignobject", "animate", "animatemotion", "animatetransform",
        "animatecolor", "set", "discard",
    ];

    public static Task<SvgPreflightResult> InspectAsync(
        MarkdownSvgOpenRequest request,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
        , SvgProviderSnapshotLedger? ownershipLedger = null
        , SvgSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgRenderDemand? demand = null
        , Action? beforeFirstNormalizedSegmentAllocation = null
        , SvgResourceInspectionWorkspacePool? inspectionWorkspaces = null
        )
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> input = request.SanitizedSvgBytes;
        MarkdownSvgRenderPriority sourcePriority = request.Priority;
        if (input.IsEmpty)
            throw SvgPreflightFailures.Unsupported("The SVG source is empty.");
        if (input.Length > options.MaxSourceBytes)
            throw SvgPreflightFailures.Resource("The SVG source exceeds the configured byte ceiling.");

        cancellationToken.ThrowIfCancellationRequested();
        return RunInspectCoreAsync(
            () =>
            {
                SvgProviderSnapshotLedger.Lease? ownership = null;
                Action? allocationObserver = beforeFirstNormalizedSegmentAllocation;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Observe the designated private snapshot before its clone.
                    // This is not admission: caller buffers, normalization overlap,
                    // decoded resources and mappings have separate ownership.
                    ownership = ownershipLedger?.Track(input.Length);
                    // Diagnostic listeners can reenter and cancel during Track.
                    // Check again before the source-sized allocation/copy.
                    cancellationToken.ThrowIfCancellationRequested();
                    // Keep a stable provider-owned snapshot, but do the potentially
                    // multi-megabyte copy on the same worker path as XML inspection.
                    // Large snapshots are private native storage so a completed
                    // inspection does not leave a source-sized managed array for GC.
                    SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(input.Span, cancellationToken);
                    Task<SvgPreflightResult> inspection = InspectCoreAsync(
                        source, options, cancellationToken
                        , ownership
                        , snapshotAdmission
                        , demand
                        , sourcePriority
                        , allocationObserver
                        , inspectionWorkspaces
                        );
                    allocationObserver = null;
                    ownership = null; // The inspection owns its terminal cleanup.
                    return inspection;
                }
                finally
                {
                    // A completed dispatch task must not keep caller memory alive.
                    input = default;
                    demand = null;
                    beforeFirstNormalizedSegmentAllocation = null;
                    ownership?.Dispose();
                    ownershipLedger = null;
                }
            },
            cancellationToken);
    }

    internal static Task<TResult> RunInspectCoreAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TResult>(cancellationToken);

        long workId = SvgWorkerStageTrace.BeginWork();
        if (workId == 0)
        {
            return Thread.CurrentThread.IsThreadPoolThread
                ? InlineTaskDispatch.Invoke(operation, cancellationToken)
                : Task.Run(operation, cancellationToken);
        }

        long dispatchStartedAt = SvgWorkerStageTrace.Start(workId);
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            SvgWorkerStageTrace.Record(
                workId,
                SvgWorkerStageId.PreflightDispatchQueueWait,
                workerInstanceId: 0,
                requestId: 0,
                documentId: 0,
                startedAt: dispatchStartedAt,
                workerCpuTicks: -1);
            return InlineTaskDispatch.Invoke(operation, cancellationToken);
        }

        return Task.Run(() =>
        {
            SvgWorkerStageTrace.Record(
                workId,
                SvgWorkerStageId.PreflightDispatchQueueWait,
                workerInstanceId: 0,
                requestId: 0,
                documentId: 0,
                startedAt: dispatchStartedAt,
                workerCpuTicks: -1);
            return operation();
        }, cancellationToken);
    }

    internal static async Task<SvgPreflightResult> InspectCoreAsync(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
        , SvgProviderSnapshotLedger.Lease? ownership = null
        , SvgSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgRenderDemand? demand = null
        , MarkdownSvgRenderPriority sourcePriority = MarkdownSvgRenderPriority.Visible
        , Action? beforeFirstNormalizedSegmentAllocation = null
        , SvgResourceInspectionWorkspacePool? inspectionWorkspaces = null
        )
    {
        ArgumentNullException.ThrowIfNull(source);
        SvgSourceBuffer snapshot = SvgSourceBuffer.CopyFrom(source, cancellationToken);
        return await InspectCoreAsync(
            snapshot,
            options,
            cancellationToken
            , ownership
            , snapshotAdmission
            , demand
            , sourcePriority
            , beforeFirstNormalizedSegmentAllocation
            , inspectionWorkspaces).ConfigureAwait(false);
    }

    // This method stays whole because its try/finally is the single ownership
    // region for the source snapshot, ledger lease and snapshot reservation (a
    // third of its lines are the parameter list and comments); the
    // normalization await swaps the snapshot inside it in a fixed order
    // (detach the normalized buffer, retire the original, then dispose the
    // producer), and an async step could not take those locals by reference.
    internal static async Task<SvgPreflightResult> InspectCoreAsync(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
        , SvgProviderSnapshotLedger.Lease? ownership = null
        , SvgSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgRenderDemand? demand = null
        , MarkdownSvgRenderPriority sourcePriority = MarkdownSvgRenderPriority.Visible
        , Action? beforeFirstNormalizedSegmentAllocation = null
        , SvgResourceInspectionWorkspacePool? inspectionWorkspaces = null
        )
    {
        SvgSourceBuffer? sourceBuffer = source;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SvgPreflightScan scan = default;
            SvgPreflightDocumentScanner.ScanDocument(source, options, inspectionWorkspaces, ref scan, cancellationToken);

            if (!scan.SawRoot)
                throw SvgPreflightFailures.Unsupported("The SVG has no document root.");

            bool requiresUtf8Normalization;
            using (SvgSourceReadLease sourceLease = source.AcquireReadLease())
            {
                requiresUtf8Normalization = SvgUtf8Normalization.RequiresUtf8Normalization(
                    sourceLease.GetSpanAt(0), scan.DeclaredEncoding);
            }

            if (requiresUtf8Normalization)
            {
                try
                {
                    if (snapshotAdmission is not null)
                    {
                        ReserveNormalizationPeak(
                            snapshotAdmission,
                            options,
                            demand,
                            sourcePriority,
                            cancellationToken);
                    }

                    using BoundedOutputStream normalized = await SvgUtf8Normalization.NormalizeToUtf8SegmentsAsync(
                        sourceBuffer ?? throw new InvalidOperationException("The source snapshot was retired before normalization."),
                        options,
                        cancellationToken,
                        beforeFirstNormalizedSegmentAllocation).ConfigureAwait(false);
                    beforeFirstNormalizedSegmentAllocation = null;
                    // The producer has drained and released its reader/input.
                    // Retire this frame's original private snapshot before taking
                    // ownership of the already-bounded normalized segments.
                    SvgSourceBuffer normalizedSource = normalized.DetachSourceBuffer();
                    sourceBuffer?.Dispose();
                    sourceBuffer = normalizedSource;
                }
                catch (XmlException exception)
                {
                    throw SvgPreflightFailures.Unsupported("The SVG could not be normalized from its declared source encoding.", exception);
                }
            }
            else
            {
                // The original provider-owned immutable snapshot is already the
                // final UTF-8 source; retain that exact storage without another copy.
            }

            return CreateResult(ref sourceBuffer, in scan, ref ownership, ref snapshotAdmission);
        }
        finally
        {
            // Failed and canceled inspection tasks can outlive their callers.
            // Release their private snapshot references on every exit while a
            // successful result independently owns the sanitized bytes.
            sourceBuffer?.Dispose();
            sourceBuffer = null;
            ownership?.Dispose();
            ownership = null;
            snapshotAdmission?.Dispose();
            snapshotAdmission = null;
            demand = null;
            beforeFirstNormalizedSegmentAllocation = null;
        }
    }

    /// <summary>
    /// Grows the open's snapshot reservation to cover UTF-8 normalization's
    /// peak before the first output segment is allocated.
    /// </summary>
    /// <param name="snapshotAdmission">The open's snapshot reservation.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="demand">The host's render demand, if any.</param>
    /// <param name="sourcePriority">The open request's priority.</param>
    /// <param name="cancellationToken">Observed before reporting capacity pressure.</param>
    /// <exception cref="SvgPreflightCapacityUnavailableException">The process capacity cannot cover the peak.</exception>
    private static void ReserveNormalizationPeak(
        SvgSnapshotAdmission.Lease snapshotAdmission,
        ResvgMarkdownSvgRendererOptions options,
        MarkdownSvgRenderDemand? demand,
        MarkdownSvgRenderPriority sourcePriority,
        CancellationToken cancellationToken)
    {
        // Normalization overlaps the initial private clone with
        // segmented output, plus a bounded tail compaction at
        // detach. Reserve that peak before the first output
        // segment is allocated. The input clone remains covered
        // by its original exact-length source reservation.
        long requiredReservationBytes = checked(
            2L * options.MaxSourceBytes + SvgSourceBuffer.SegmentSize);
        MarkdownSvgRenderPriority priority = demand?.Priority ?? sourcePriority;
        bool admitted = snapshotAdmission.TryGrow(
            requiredReservationBytes,
            priority,
            out long observedCapacityVersion);
        if (!admitted && priority == MarkdownSvgRenderPriority.Overscan &&
            demand?.Priority == MarkdownSvgRenderPriority.Visible)
        {
            // Promotion can race the first admission attempt.
            // Retry once in the monotonic visible tier; never
            // demote or wait while retaining the source clone.
            admitted = snapshotAdmission.TryGrow(
                requiredReservationBytes,
                MarkdownSvgRenderPriority.Visible,
                out observedCapacityVersion);
        }

        if (!admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SvgPreflightCapacityUnavailableException(
                observedCapacityVersion,
                requiredReservationBytes);
        }
    }

    /// <summary>
    /// Seals and hashes the final source, publishes its owned capacity, and
    /// transfers the source, ownership ticket and reservation to the result.
    /// </summary>
    /// <param name="sourceBuffer">The final source; cleared once the result owns it.</param>
    /// <param name="scan">The completed scan.</param>
    /// <param name="ownership">The provider ownership ticket; cleared once attached.</param>
    /// <param name="snapshotAdmission">The snapshot reservation; cleared once attached.</param>
    /// <returns>The accepted preflight result.</returns>
    private static SvgPreflightResult CreateResult(
        ref SvgSourceBuffer? sourceBuffer,
        in SvgPreflightScan scan,
        ref SvgProviderSnapshotLedger.Lease? ownership,
        ref SvgSnapshotAdmission.Lease? snapshotAdmission)
    {
        SvgSourceBuffer finalSource = sourceBuffer ??
            throw new InvalidOperationException("The SVG source snapshot was not produced.");

        finalSource.Seal();
        // Raster signatures are authoritative in both host validation and the
        // worker's data-image resolver. Do not reparse/serialize the whole XML
        // document merely to repair MIME headers: that duplicates payload work,
        // expands the source, and does not reach nested SVG or CSS references.
        byte[] hash = finalSource.ComputeSha256();
        // The retained ticket measures actual owned byte arrays. The
        // normalized tail was compacted before transfer, so this should be
        // the final source-byte capacity, not a logical-length estimate.
        ownership?.SetLength(finalSource.AllocatedCapacity);
        if (snapshotAdmission is not null)
            snapshotAdmission.SetLength(finalSource.AllocatedCapacity);

        double? ratio = scan.Width > 0 && scan.Height > 0 ? scan.Width / scan.Height : null;
        var result = new SvgPreflightResult(
            finalSource,
            hash,
            new MarkdownSvgDocumentInfo(scan.Width, scan.Height, ratio, scan.Description, scan.HasText, scan.UsesCurrentColor, scan.UsesColorScheme),
            scan.StructuralCost,
            scan.EmbeddedBytes,
            scan.EmbeddedPixels,
            scan.FilterPrimitives);
        try
        {
            if (ownership is not null)
            {
                result.AttachOwnership(ownership);
                ownership = null;
            }
            if (snapshotAdmission is not null)
            {
                result.AttachSnapshotAdmission(snapshotAdmission);
                snapshotAdmission = null;
            }
        }
        catch
        {
            result.ReleaseSource();
            throw;
        }
        sourceBuffer = null; // The result now owns the exact same storage.
        return result;
    }

    internal static XmlReaderSettings CreateReaderSettings(
        long maxCharacters,
        bool ignoreComments,
        bool asynchronous = true) => new()
    {
        Async = asynchronous,
        // Parse the declaration only far enough to identify and reject it.
        // No resolver is available, and entity expansion is tightly bounded.
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        IgnoreComments = ignoreComments,
        IgnoreProcessingInstructions = false,
        MaxCharactersInDocument = maxCharacters,
        MaxCharactersFromEntities = 1,
    };
}
