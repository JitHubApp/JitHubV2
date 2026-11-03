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
            SvgPreflightScan scan = default;
            ScanDocument(source, options, inspectionWorkspaces, ref scan, cancellationToken);

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

    // The outer document's own nesting level for embedded-resource checks.
    private const int OuterSvgDepth = 0;

    /// <summary>
    /// Parses the whole source once, enforcing every structural, element,
    /// attribute and embedded-resource rule, and records the metadata the
    /// result reports. The reader, its input and the inspection workspace are
    /// released on every exit.
    /// </summary>
    /// <param name="source">The private source snapshot.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="inspectionWorkspaces">The renderer's reusable inspection workspaces, if any.</param>
    /// <param name="scan">Receives the totals and metadata.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    private static void ScanDocument(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionWorkspacePool? inspectionWorkspaces,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        IDisposable? readerInput = null;
        XmlReader? reader = null;
        var resources = new SvgResourceInspectionContext(inspectionWorkspaces);
        char[]? attributeScratch = null;
        bool readNext = true;

        try
        {
            XmlReaderSettings settings = CreateReaderSettings(
                options.MaxSourceBytes, ignoreComments: true, asynchronous: false);
            reader = OpenReader(source, settings, ref readerInput, cancellationToken);
            while (true)
            {
                if (readNext && !reader.Read())
                    break;
                readNext = ScanNode(reader, options, resources, ref attributeScratch, ref scan, cancellationToken);
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
    }

    /// <summary>Creates the synchronous reader over a read lease of the source.</summary>
    /// <param name="source">The private source snapshot.</param>
    /// <param name="settings">The hardened reader settings.</param>
    /// <param name="readerInput">Receives the text reader or stream that owns the lease, as soon as it exists.</param>
    /// <param name="cancellationToken">Cancels reads.</param>
    /// <returns>The reader.</returns>
    private static XmlReader OpenReader(
        SvgSourceBuffer source,
        XmlReaderSettings settings,
        ref IDisposable? readerInput,
        CancellationToken cancellationToken)
    {
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
                return XmlReader.Create(textInput, settings);
            }
            else
            {
                // Preserve XmlReader's BOM/declaration sniffing for small
                // sources and non-UTF-8 encodings, while the stream retains
                // a lease over either array or native-backed storage.
                var stream = new SvgSourceStream(readerLease, cancellationToken);
                readerLease = null;
                readerInput = stream;
                return XmlReader.Create(stream, settings);
            }
        }
        finally
        {
            readerLease?.Dispose();
        }
    }

    /// <summary>Checks the reader's current node.</summary>
    /// <param name="reader">The reader, positioned on the node.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>False when the reader is already positioned on the next node.</returns>
    private static bool ScanNode(
        XmlReader reader,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.Depth > options.MaxXmlDepth)
            throw SvgPreflightFailures.Resource("The SVG exceeds the XML-depth ceiling.");

        if (reader.NodeType == XmlNodeType.DocumentType)
        {
            throw SvgPreflightFailures.Unsupported("SVG DTDs and entities are forbidden.");
        }

        if (reader.NodeType == XmlNodeType.XmlDeclaration)
        {
            scan.DeclaredEncoding = reader.GetAttribute("encoding");
            return true;
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
            scan.SvgNesting--;
            return true;
        }

        if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
            XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            SvgPreflightStructure.ChargeStructuralPayload(
                reader.Value,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted text-content ceiling.");
            return true;
        }

        if (reader.NodeType != XmlNodeType.Element)
            return true;

        return ScanElement(reader, options, resources, ref attributeScratch, ref scan, cancellationToken);
    }

    /// <summary>Checks one element, its attributes and any text content the scan consumes.</summary>
    /// <param name="reader">The reader, positioned on the element.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>False when the element's content was read and the reader is on the next node.</returns>
    private static bool ScanElement(
        XmlReader reader,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        string name = reader.LocalName;
        if (!scan.SawRoot)
        {
            if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
            {
                throw SvgPreflightFailures.Unsupported("The document root is not an SVG element.");
            }
            scan.SawRoot = true;
        }

        if (ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported($"The static SVG subset does not allow <{name}>.");
        bool isSvgElement = reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal);
        if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            scan.SvgNesting++;
            if (scan.SvgNesting > options.MaxNestedSvgDepth)
                throw SvgPreflightFailures.Resource("The SVG exceeds the nested-SVG ceiling.");
        }

        // Native inspection uses local names regardless of namespace.
        // Preserve that conservative font-discovery parity for foreign
        // wrappers that still contain renderable text.
        scan.HasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("textPath", StringComparison.OrdinalIgnoreCase);
        bool isFilter = name.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
        if (isFilter)
            scan.FilterPrimitives++;

        SvgPreflightStructure.ChargeStructural(
            1 + (isFilter ? 31 : 0),
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted structural ceiling.");
        if (reader.Depth == 0)
        {
            scan.Width = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("width"));
            scan.Height = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("height"));
        }

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                ScanAttribute(reader, name, isSvgElement, options, resources, ref attributeScratch, ref scan, cancellationToken);
            }
            reader.MoveToElement();
        }

        if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
        {
            string css = reader.ReadElementContentAsString();
            SvgPreflightStructure.ChargeStructuralPayload(
                css,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted CSS ceiling.");
            SvgPreflightCss.InspectCss(
                css,
                options,
                resources,
                ref scan.EmbeddedBytes,
                ref scan.EmbeddedPixels,
                cancellationToken,
                OuterSvgDepth,
                ref scan.HasText,
                ref scan.UsesCurrentColor,
                ref scan.UsesColorScheme,
                trackThemeMetadata: isSvgElement);
            // ReadElementContentAsString leaves the reader on the
            // next node. Process that node before asking the reader to
            // advance again, or adjacent security-sensitive elements
            // and metadata would be silently skipped.
            return false;
        }

        if (reader.Depth == 1 && name.Equals("desc", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
        {
            string text = reader.ReadElementContentAsString();
            SvgPreflightStructure.ChargeStructuralPayload(
                text,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted description ceiling.");
            scan.Description ??= text.Length <= 4096 ? text.Trim() : text[..4096].Trim();
            return false;
        }

        if (reader.IsEmptyElement && isSvgElement &&
            name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            scan.SvgNesting--;
        return true;
    }

    /// <summary>Charges and checks the attribute the reader is positioned on.</summary>
    /// <param name="reader">The reader, positioned on the attribute.</param>
    /// <param name="elementName">The owning element's local name.</param>
    /// <param name="isSvgElement">Whether the owning element is in the SVG namespace.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    private static void ScanAttribute(
        XmlReader reader,
        string elementName,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        string attributeName = reader.LocalName;
        SvgPreflightStructure.ChargeStructuralPayload(
            attributeName,
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted attribute ceiling.");
        // Only the outer parser borrows this destination.
        // Nested SVGs need independent scratch while the
        // referring attribute span is still being checked.
        attributeScratch ??= resources.GetAttributeScratch(checked(options.MaxSourceBytes + 2));
        ReadOnlySpan<char> rawValue = SvgPreflightStructure.ReadAttributeValue(
            reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken);
        if (elementName.Equals("path", StringComparison.OrdinalIgnoreCase) &&
            reader.Name.Equals("d", StringComparison.Ordinal))
        {
            // Match GetAttribute("d")'s exact qualified name
            // and charge the untrimmed payload without a
            // second, potentially large path-data string.
            SvgPreflightStructure.ChargeStructural(SvgPreflightStructure.ComputePathComplexity(rawValue), options, ref scan.StructuralCost,
                "The SVG exceeds the weighted path-complexity ceiling.");
        }
        ReadOnlySpan<char> value = rawValue.Trim();
        SvgPreflightStructure.ChargeStructuralPayload(
            value,
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted attribute-value ceiling.");
        if (attributeName.Length > 2 && attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported("SVG event handlers are forbidden.");
        InspectAttributeContent(elementName, attributeName, value, isSvgElement, options, resources, ref scan, cancellationToken);
    }

    /// <summary>Records theme metadata and inspects any reference or CSS an attribute value carries.</summary>
    /// <param name="elementName">The owning element's local name.</param>
    /// <param name="attributeName">The attribute's local name.</param>
    /// <param name="value">The trimmed attribute value.</param>
    /// <param name="isSvgElement">Whether the owning element is in the SVG namespace.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    private static void InspectAttributeContent(
        string elementName,
        string attributeName,
        ReadOnlySpan<char> value,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        if (isSvgElement && attributeName.Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            scan.UsesColorScheme |= SvgColorSchemeUsage.UsesPrefersColorSchemeMediaCondition(SvgPreflightCss.DecodeCssEscapes(value.ToString()));
        }
        else if (isSvgElement && SvgColorSchemeUsage.IsCurrentColorPresentationAttribute(attributeName))
        {
            scan.UsesCurrentColor |= SvgPreflightCss.ContainsCssIdentifier(SvgPreflightCss.DecodeCssEscapes(value.ToString()), "currentColor");
        }
        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            SvgEmbeddedReferenceInspection.InspectElementReference(
                elementName,
                attributeName,
                value,
                options,
                resources,
                ref scan.EmbeddedBytes,
                ref scan.EmbeddedPixels,
                cancellationToken,
                OuterSvgDepth,
                ref scan.HasText,
                ref scan.UsesCurrentColor,
                ref scan.UsesColorScheme);
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
                ref scan.EmbeddedBytes,
                ref scan.EmbeddedPixels,
                cancellationToken,
                OuterSvgDepth,
                ref scan.HasText,
                ref scan.UsesCurrentColor,
                ref scan.UsesColorScheme,
                attributeName.Equals("style", StringComparison.OrdinalIgnoreCase),
                isSvgElement);
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
        SvgProcessSnapshotAdmission.Lease snapshotAdmission,
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
        ref SvgProcessSnapshotAdmission.Lease? snapshotAdmission)
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
