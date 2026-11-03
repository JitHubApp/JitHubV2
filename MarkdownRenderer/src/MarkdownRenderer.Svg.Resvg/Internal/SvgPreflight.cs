using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

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
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
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
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
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

    internal static async Task<SvgPreflightResult> InspectCoreAsync(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
        , SvgProviderSnapshotLedger.Lease? ownership = null
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
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
            IDisposable? readerInput = null;
            XmlReader? reader = null;
            int structuralCost = 0;
            int svgNesting = 0;
            int embeddedSvgDepth = 0;
            int filterPrimitives = 0;
            bool sawRoot = false;
            bool hasText = false;
            bool usesCurrentColor = false;
            bool usesColorScheme = false;
            string? declaredEncoding = null;
            double? width = null;
            double? height = null;
            string? description = null;
            long embeddedBytes = 0;
            long embeddedPixels = 0;
            var resources = new SvgResourceInspectionContext(inspectionWorkspaces);
            char[]? attributeScratch = null;
            bool readNext = true;

            try
            {
                XmlReaderSettings settings = CreateReaderSettings(
                    options.MaxSourceBytes, ignoreComments: true, asynchronous: false);
                SvgSourceReadLease? readerLease = source.AcquireReadLease();
                try
                {
                    int utf8Offset = readerLease.GetSpanAt(0).StartsWith<byte>([0xef, 0xbb, 0xbf]) ? 3 : 0;
                    if (readerLease.Length >= 4096 &&
                        !SvgUtf8Normalization.HasNonUtf8EncodingPrefix(readerLease.GetSpanAt(0)) &&
                        !readerLease.GetSpanAt(utf8Offset).StartsWith("<?xml"u8))
                    {
                        // The snapshot is already private memory. Decode directly
                        // into XmlReader's character buffer without a second 4 KiB
                        // byte buffer. The text reader owns the lease through parse.
                        var textInput = new SvgUtf8TextReader(readerLease, utf8Offset, cancellationToken);
                        readerLease = null;
                        readerInput = textInput;
                        reader = XmlReader.Create(textInput, settings);
                    }
                    else
                    {
                        // Preserve XmlReader's BOM/declaration sniffing for small
                        // sources and non-UTF-8 encodings, while the stream retains
                        // a lease over either array or native-backed storage.
                        var stream = new SvgSourceStream(readerLease, cancellationToken);
                        readerLease = null;
                        readerInput = stream;
                        reader = XmlReader.Create(stream, settings);
                    }
                }
                finally
                {
                    readerLease?.Dispose();
                }
                while (true)
                {
                    if (readNext && !reader.Read())
                        break;
                    readNext = true;

                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.Depth > options.MaxXmlDepth)
                        throw SvgPreflightFailures.Resource("The SVG exceeds the XML-depth ceiling.");

                    if (reader.NodeType == XmlNodeType.DocumentType)
                    {
                        throw SvgPreflightFailures.Unsupported("SVG DTDs and entities are forbidden.");
                    }

                    if (reader.NodeType == XmlNodeType.XmlDeclaration)
                    {
                        declaredEncoding = reader.GetAttribute("encoding");
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                        reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
                    {
                        throw SvgPreflightFailures.Unsupported("External SVG stylesheets are forbidden.");
                    }

                    if (reader.NodeType == XmlNodeType.EndElement &&
                        reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                        reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
                    {
                        svgNesting--;
                        continue;
                    }

                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                        XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    {
                        SvgPreflightStructure.ChargeStructuralPayload(
                            reader.Value,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted text-content ceiling.");
                        continue;
                    }

                    if (reader.NodeType != XmlNodeType.Element)
                        continue;

                    string name = reader.LocalName;
                    if (!sawRoot)
                    {
                        if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                            !reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
                        {
                            throw SvgPreflightFailures.Unsupported("The document root is not an SVG element.");
                        }
                        sawRoot = true;
                    }

                    if (ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                        throw SvgPreflightFailures.Unsupported($"The static SVG subset does not allow <{name}>.");
                    bool isSvgElement = reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal);
                    if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                    {
                        svgNesting++;
                        if (svgNesting > options.MaxNestedSvgDepth)
                            throw SvgPreflightFailures.Resource("The SVG exceeds the nested-SVG ceiling.");
                    }

                    // Native inspection uses local names regardless of namespace.
                    // Preserve that conservative font-discovery parity for foreign
                    // wrappers that still contain renderable text.
                    hasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("textPath", StringComparison.OrdinalIgnoreCase);
                    bool isFilter = name.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
                    if (isFilter)
                        filterPrimitives++;

                    SvgPreflightStructure.ChargeStructural(
                        1 + (isFilter ? 31 : 0),
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted structural ceiling.");
                    if (reader.Depth == 0)
                    {
                        width = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("width"));
                        height = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("height"));
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            string attributeName = reader.LocalName;
                            SvgPreflightStructure.ChargeStructuralPayload(
                                attributeName,
                                options,
                                ref structuralCost,
                                "The SVG exceeds the weighted attribute ceiling.");
                            // Only the outer parser borrows this destination.
                            // Nested SVGs need independent scratch while the
                            // referring attribute span is still being checked.
                            attributeScratch ??= resources.GetAttributeScratch(checked(options.MaxSourceBytes + 2));
                            ReadOnlySpan<char> rawValue = SvgPreflightStructure.ReadAttributeValue(
                                reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken);
                            if (name.Equals("path", StringComparison.OrdinalIgnoreCase) &&
                                reader.Name.Equals("d", StringComparison.Ordinal))
                            {
                                // Match GetAttribute("d")'s exact qualified name
                                // and charge the untrimmed payload without a
                                // second, potentially large path-data string.
                                SvgPreflightStructure.ChargeStructural(SvgPreflightStructure.ComputePathComplexity(rawValue), options, ref structuralCost,
                                    "The SVG exceeds the weighted path-complexity ceiling.");
                            }
                            ReadOnlySpan<char> value = rawValue.Trim();
                            SvgPreflightStructure.ChargeStructuralPayload(
                                value,
                                options,
                                ref structuralCost,
                                "The SVG exceeds the weighted attribute-value ceiling.");
                            if (attributeName.Length > 2 && attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                                throw SvgPreflightFailures.Unsupported("SVG event handlers are forbidden.");
                            if (isSvgElement && attributeName.Equals("media", StringComparison.OrdinalIgnoreCase))
                            {
                                usesColorScheme |= SvgColorSchemeUsage.UsesPrefersColorSchemeMediaCondition(SvgPreflightCss.DecodeCssEscapes(value.ToString()));
                            }
                            else if (isSvgElement && SvgColorSchemeUsage.IsCurrentColorPresentationAttribute(attributeName))
                            {
                                usesCurrentColor |= SvgPreflightCss.ContainsCssIdentifier(SvgPreflightCss.DecodeCssEscapes(value.ToString()), "currentColor");
                            }
                            if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                                attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                SvgEmbeddedReferenceInspection.InspectElementReference(
                                    name,
                                    attributeName,
                                    value,
                                    options,
                                    resources,
                                    ref embeddedBytes,
                                    ref embeddedPixels,
                                    cancellationToken,
                                    embeddedSvgDepth,
                                    ref hasText,
                                    ref usesCurrentColor,
                                    ref usesColorScheme);
                            }
                            // Only CSS-bearing values use CSS quoting rules. Treating
                            // arbitrary metadata (for example an aria-label containing
                            // an apostrophe) as a stylesheet caused valid SVGs to fail
                            // with an unterminated-string diagnostic. Decode escapes
                            // first so resource-bearing presentation attributes still
                            // cannot hide url() or active CSS directives.
                            if (SvgPreflightCss.MayContainCssResourceOrActiveContent(attributeName, value))
                            {
                                SvgPreflightCss.InspectCss(
                                    value.ToString(),
                                    options,
                                    resources,
                                    ref embeddedBytes,
                                    ref embeddedPixels,
                                    cancellationToken,
                                    embeddedSvgDepth,
                                    ref hasText,
                                    ref usesCurrentColor,
                                    ref usesColorScheme,
                                    attributeName.Equals("style", StringComparison.OrdinalIgnoreCase),
                                    isSvgElement);
                            }
                        }
                        reader.MoveToElement();
                    }

                    if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                    {
                        string css = reader.ReadElementContentAsString();
                        SvgPreflightStructure.ChargeStructuralPayload(
                            css,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted CSS ceiling.");
                        SvgPreflightCss.InspectCss(
                            css,
                            options,
                            resources,
                            ref embeddedBytes,
                            ref embeddedPixels,
                            cancellationToken,
                            embeddedSvgDepth,
                            ref hasText,
                            ref usesCurrentColor,
                            ref usesColorScheme,
                            trackThemeMetadata: isSvgElement);
                        // ReadElementContentAsString leaves the reader on the
                        // next node. Process that node before asking the reader to
                        // advance again, or adjacent security-sensitive elements
                        // and metadata would be silently skipped.
                        readNext = false;
                        continue;
                    }

                    if (reader.Depth == 1 && name.Equals("desc", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                    {
                        string text = reader.ReadElementContentAsString();
                        SvgPreflightStructure.ChargeStructuralPayload(
                            text,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted description ceiling.");
                        description ??= text.Length <= 4096 ? text.Trim() : text[..4096].Trim();
                        readNext = false;
                        continue;
                    }

                    if (reader.IsEmptyElement && isSvgElement &&
                        name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                        svgNesting--;
                }
            }
            catch (MarkdownSvgException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is XmlException or FormatException or OverflowException or DecoderFallbackException)
            {
                throw SvgPreflightFailures.Unsupported("The SVG is malformed.", exception);
            }
            finally
            {
                try
                {
                    reader?.Dispose();
                }
                finally
                {
                    reader = null;
                    try
                    {
                        readerInput?.Dispose();
                    }
                    finally
                    {
                        readerInput = null;
                        attributeScratch = null;
                        resources.Dispose();
                    }
                }
            }

            if (!sawRoot)
                throw SvgPreflightFailures.Unsupported("The SVG has no document root.");

            bool requiresUtf8Normalization;
            using (SvgSourceReadLease sourceLease = source.AcquireReadLease())
            {
                requiresUtf8Normalization = SvgUtf8Normalization.RequiresUtf8Normalization(
                    sourceLease.GetSpanAt(0), declaredEncoding);
            }

            if (requiresUtf8Normalization)
            {
                try
                {
                    if (snapshotAdmission is not null)
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

            double? ratio = width > 0 && height > 0 ? width / height : null;
            var result = new SvgPreflightResult(
                finalSource,
                hash,
                new MarkdownSvgDocumentInfo(width, height, ratio, description, hasText, usesCurrentColor, usesColorScheme),
                structuralCost,
                embeddedBytes,
                embeddedPixels,
                filterPrimitives);
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
