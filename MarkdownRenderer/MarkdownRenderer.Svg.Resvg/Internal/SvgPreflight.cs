using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Holds the provider-owned bytes through preflight, worker attachment and the
/// document's admitted render lifetime. Retained completed inspection tasks can
/// refer to this holder without pinning bytes after the document retires it.
/// Metadata remains readable after source retirement.
/// </summary>
internal sealed class SvgPreflightResult(
    SvgSourceBuffer Source,
    byte[] Hash,
    MarkdownSvgDocumentInfo Info,
    int StructuralCost,
    long EmbeddedBytes,
    long EmbeddedPixels,
    int FilterPrimitives)
{
    private SvgSourceBuffer? _source = Source;
    private SvgProcessSnapshotAdmission.Lease? _snapshotAdmission;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private SvgProviderSnapshotLedger.Lease? _ownership;

    internal void AttachOwnership(SvgProviderSnapshotLedger.Lease ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (Interlocked.CompareExchange(ref _ownership, ownership, null) is not null)
            throw new InvalidOperationException("The SVG snapshot already has an ownership ticket.");
        try
        {
            Source.AttachRetirementResource(ownership);
        }
        catch
        {
            Interlocked.CompareExchange(ref _ownership, null, ownership);
            throw;
        }
    }
#endif

    internal long SourceReservationBytes => Volatile.Read(ref _snapshotAdmission)?.Bytes ?? 0;

    internal void AttachSnapshotAdmission(SvgProcessSnapshotAdmission.Lease admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (Interlocked.CompareExchange(ref _snapshotAdmission, admission, null) is not null)
            throw new InvalidOperationException("The SVG snapshot already has a process admission reservation.");
        try
        {
            Source.AttachRetirementResource(admission);
        }
        catch
        {
            Interlocked.CompareExchange(ref _snapshotAdmission, null, admission);
            throw;
        }
    }

    public SvgSourceBuffer Source => Volatile.Read(ref _source) ?? SvgSourceBuffer.Empty;
    public byte[] Hash { get; } = Hash;
    public MarkdownSvgDocumentInfo Info { get; } = Info;
    public int StructuralCost { get; } = StructuralCost;
    public long EmbeddedBytes { get; } = EmbeddedBytes;
    public long EmbeddedPixels { get; } = EmbeddedPixels;
    public int FilterPrimitives { get; } = FilterPrimitives;

    // Only the owning open operation (on failure), or a successfully transferred
    // document after its render/cancellation barrier drains, may retire bytes.
    // WorkerPool borrows this holder and must never release it independently.
    internal void ReleaseSource()
    {
        Interlocked.Exchange(ref _source, null)?.Dispose();
        // Snapshot reservations and diagnostic ownership are attached to the
        // source storage itself. Owner retirement refunds them only after every
        // active read lease has drained and native bytes are actually freed.
    }
}

internal static class SvgPreflight
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Keeps byte/pixel accounting deduplicated by payload while retaining
    /// bounded context-sensitive validation for nested SVGs. A shared payload
    /// may be reached at different nesting depths, and each depth can expose a
    /// different over-limit descendant path.
    /// </summary>
    private sealed class ResourceInspectionContext : IDisposable
    {
        private const int MaximumPooledCollectionCapacity = 1024;
        private static ResourceInspectionWorkspace? s_workspace0;
        private static ResourceInspectionWorkspace? s_workspace1;

        private ResourceInspectionWorkspace? _workspace;

        internal ResourceInspectionContext()
        {
            ResourceInspectionWorkspace workspace =
                Interlocked.Exchange(ref s_workspace0, null) ??
                Interlocked.Exchange(ref s_workspace1, null) ??
                new ResourceInspectionWorkspace();
            _workspace = workspace;
        }

        // Per inspection, digest-only and positive raster entries: never retain
        // an XML attribute string or decoded payload. Nested SVG validation is
        // depth-sensitive and deliberately cannot take this shortcut.
        internal bool IsRasterValidated(RasterReferenceKey key) =>
            GetWorkspace().ValidatedRasterReferences.Contains(key);

        internal void RememberRaster(RasterReferenceKey key)
        {
            ResourceInspectionWorkspace workspace = GetWorkspace();
            if (workspace.ValidatedRasterReferences.Count < 256)
                workspace.ValidatedRasterReferences.Add(key);
        }

        public bool AddResource(RasterReferenceKey identity) =>
            GetWorkspace().Resources.Add(identity);

        internal char[] GetAttributeScratch(int maximumCapacity)
        {
            ResourceInspectionWorkspace workspace = GetWorkspace();
            int capacity = Math.Min(1026, maximumCapacity);
            if (workspace.AttributeScratch is not { } scratch || scratch.Length != capacity)
                workspace.AttributeScratch = scratch = new char[capacity];
            return scratch;
        }

        public bool IsNestedSvgValidatedAtDepth(RasterReferenceKey identity, int embeddedDepth) =>
            GetWorkspace().DeepestValidatedNestedSvgDepth.TryGetValue(identity, out int deepestDepth) &&
            deepestDepth >= embeddedDepth;

        public void MarkNestedSvgValidated(RasterReferenceKey identity, int embeddedDepth)
        {
            Dictionary<RasterReferenceKey, int> validated = GetWorkspace().DeepestValidatedNestedSvgDepth;
            if (!validated.TryGetValue(identity, out int deepestDepth) ||
                embeddedDepth > deepestDepth)
            {
                validated[identity] = embeddedDepth;
            }
        }

        public void Dispose()
        {
            ResourceInspectionWorkspace? workspace = Interlocked.Exchange(ref _workspace, null);
            if (workspace is null)
                return;

            // Detach the lease before making the workspace visible to another
            // inspection. A stale or repeated Dispose on this context can then
            // neither clear nor observe a later borrower's keys.
            workspace.Clear();

            if (workspace.CombinedCollectionCapacity > MaximumPooledCollectionCapacity)
                return;
            if (Interlocked.CompareExchange(ref s_workspace0, workspace, null) is null)
                return;
            _ = Interlocked.CompareExchange(ref s_workspace1, workspace, null);
        }

        private ResourceInspectionWorkspace GetWorkspace() =>
            Volatile.Read(ref _workspace) ?? throw new ObjectDisposedException(nameof(ResourceInspectionContext));

        private sealed class ResourceInspectionWorkspace
        {
            internal HashSet<RasterReferenceKey> Resources { get; } = [];
            internal Dictionary<RasterReferenceKey, int> DeepestValidatedNestedSvgDepth { get; } = [];
            internal HashSet<RasterReferenceKey> ValidatedRasterReferences { get; } = [];
            // Retain only the small initial destination, never a grown data-URI
            // or path buffer. Two workspaces retain at most 4,104 char bytes.
            internal char[]? AttributeScratch { get; set; }

            internal long CombinedCollectionCapacity =>
                (long)Resources.EnsureCapacity(0) +
                DeepestValidatedNestedSvgDepth.EnsureCapacity(0) +
                ValidatedRasterReferences.EnsureCapacity(0);

            internal void Clear()
            {
                Resources.Clear();
                DeepestValidatedNestedSvgDepth.Clear();
                ValidatedRasterReferences.Clear();
                if (AttributeScratch is { } scratch)
                    scratch.AsSpan().Clear();
            }
        }
    }

    private readonly record struct RasterReferenceKey(ulong A, ulong B, ulong C, ulong D)
    {
        internal static RasterReferenceKey Create(ReadOnlySpan<char> reference)
            => Create(MemoryMarshal.AsBytes(reference));

        internal static RasterReferenceKey Create(ReadOnlySpan<byte> payload)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(payload, digest);
            return new(
                BinaryPrimitives.ReadUInt64LittleEndian(digest),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
        }
    }

    private static readonly string[] ForbiddenElements =
    [
        "script", "foreignobject", "animate", "animatemotion", "animatetransform",
        "animatecolor", "set", "discard",
    ];

    private static readonly HashSet<string> CurrentColorPresentationAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "fill", "flood-color", "lighting-color", "stop-color", "stroke",
        "text-decoration", "text-decoration-color",
    };

    public static Task<SvgPreflightResult> InspectAsync(
        MarkdownSvgOpenRequest request,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , SvgProviderSnapshotLedger? ownershipLedger = null
#endif
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgDemandTicket? demand = null
        , Action? beforeFirstNormalizedSegmentAllocation = null
        )
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> input = request.SanitizedSvgBytes;
        MarkdownSvgRenderPriority sourcePriority = request.Priority;
        if (input.IsEmpty)
            throw Unsupported("The SVG source is empty.");
        if (input.Length > options.MaxSourceBytes)
            throw Resource("The SVG source exceeds the configured byte ceiling.");

        cancellationToken.ThrowIfCancellationRequested();
        return RunInspectCoreAsync(
            () =>
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                SvgProviderSnapshotLedger.Lease? ownership = null;
#endif
                Action? allocationObserver = beforeFirstNormalizedSegmentAllocation;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Observe the designated private snapshot before its clone.
                    // This is not admission: caller buffers, normalization overlap,
                    // decoded resources and mappings have separate ownership.
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ownership = ownershipLedger?.Track(input.Length);
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        , ownership
#endif
                        , snapshotAdmission
                        , demand
                        , sourcePriority
                        , allocationObserver
                        );
                    allocationObserver = null;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ownership = null; // The inspection owns its terminal cleanup.
#endif
                    return inspection;
                }
                finally
                {
                    // A completed dispatch task must not keep caller memory alive.
                    input = default;
                    demand = null;
                    beforeFirstNormalizedSegmentAllocation = null;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ownership?.Dispose();
                    ownershipLedger = null;
#endif
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

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long workId = SvgWorkerStageTrace.CaptureCurrentWorkId();
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
            return operation();
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
#else
        return Thread.CurrentThread.IsThreadPoolThread
            ? operation()
            : Task.Run(operation, cancellationToken);
#endif
    }

    internal static async Task<SvgPreflightResult> InspectCoreAsync(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , SvgProviderSnapshotLedger.Lease? ownership = null
#endif
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgDemandTicket? demand = null
        , MarkdownSvgRenderPriority sourcePriority = MarkdownSvgRenderPriority.Visible
        , Action? beforeFirstNormalizedSegmentAllocation = null
        )
    {
        ArgumentNullException.ThrowIfNull(source);
        SvgSourceBuffer snapshot = SvgSourceBuffer.CopyFrom(source, cancellationToken);
        return await InspectCoreAsync(
            snapshot,
            options,
            cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , ownership
#endif
            , snapshotAdmission
            , demand
            , sourcePriority
            , beforeFirstNormalizedSegmentAllocation).ConfigureAwait(false);
    }

    internal static async Task<SvgPreflightResult> InspectCoreAsync(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , SvgProviderSnapshotLedger.Lease? ownership = null
#endif
        , SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null
        , MarkdownSvgDemandTicket? demand = null
        , MarkdownSvgRenderPriority sourcePriority = MarkdownSvgRenderPriority.Visible
        , Action? beforeFirstNormalizedSegmentAllocation = null
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
            var resources = new ResourceInspectionContext();
            char[]? attributeScratch = null;
            bool readNext = true;

            try
            {
                XmlReaderSettings settings = CreateReaderSettings(
                    options.MaxSourceBytes, ignoreComments: true, asynchronous: false);
                SvgSourceBuffer.ReadLease? readerLease = source.AcquireReadLease();
                try
                {
                    int utf8Offset = readerLease.GetSpanAt(0).StartsWith<byte>([0xef, 0xbb, 0xbf]) ? 3 : 0;
                    if (readerLease.Length >= 4096 &&
                        !HasNonUtf8EncodingPrefix(readerLease.GetSpanAt(0)) &&
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
                        throw Resource("The SVG exceeds the XML-depth ceiling.");

                    if (reader.NodeType == XmlNodeType.DocumentType)
                    {
                        throw Unsupported("SVG DTDs and entities are forbidden.");
                    }

                    if (reader.NodeType == XmlNodeType.XmlDeclaration)
                    {
                        declaredEncoding = reader.GetAttribute("encoding");
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                        reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unsupported("External SVG stylesheets are forbidden.");
                    }

                    if (reader.NodeType == XmlNodeType.EndElement &&
                        reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                        reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
                    {
                        svgNesting--;
                        continue;
                    }

                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                        XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    {
                        ChargeStructuralPayload(
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
                            !reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
                        {
                            throw Unsupported("The document root is not an SVG element.");
                        }
                        sawRoot = true;
                    }

                    if (ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                        throw Unsupported($"The static SVG subset does not allow <{name}>.");
                    bool isSvgElement = reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal);
                    if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                    {
                        svgNesting++;
                        if (svgNesting > options.MaxNestedSvgDepth)
                            throw Resource("The SVG exceeds the nested-SVG ceiling.");
                    }

                    // Native inspection uses local names regardless of namespace.
                    // Preserve that conservative font-discovery parity for foreign
                    // wrappers that still contain renderable text.
                    hasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("textPath", StringComparison.OrdinalIgnoreCase);
                    bool isFilter = name.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
                    if (isFilter)
                        filterPrimitives++;

                    ChargeStructural(
                        1 + (isFilter ? 31 : 0),
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted structural ceiling.");
                    if (reader.Depth == 0)
                    {
                        width = ParseCssLength(reader.GetAttribute("width"));
                        height = ParseCssLength(reader.GetAttribute("height"));
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            string attributeName = reader.LocalName;
                            ChargeStructuralPayload(
                                attributeName,
                                options,
                                ref structuralCost,
                                "The SVG exceeds the weighted attribute ceiling.");
                            // Only the outer parser borrows this destination.
                            // Nested SVGs need independent scratch while the
                            // referring attribute span is still being checked.
                            attributeScratch ??= resources.GetAttributeScratch(checked(options.MaxSourceBytes + 2));
                            ReadOnlySpan<char> rawValue = ReadAttributeValue(
                                reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken);
                            if (name.Equals("path", StringComparison.OrdinalIgnoreCase) &&
                                reader.Name.Equals("d", StringComparison.Ordinal))
                            {
                                // Match GetAttribute("d")'s exact qualified name
                                // and charge the untrimmed payload without a
                                // second, potentially large path-data string.
                                ChargeStructural(ComputePathComplexity(rawValue), options, ref structuralCost,
                                    "The SVG exceeds the weighted path-complexity ceiling.");
                            }
                            ReadOnlySpan<char> value = rawValue.Trim();
                            ChargeStructuralPayload(
                                value,
                                options,
                                ref structuralCost,
                                "The SVG exceeds the weighted attribute-value ceiling.");
                            if (attributeName.Length > 2 && attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                                throw Unsupported("SVG event handlers are forbidden.");
                            if (isSvgElement && attributeName.Equals("media", StringComparison.OrdinalIgnoreCase))
                            {
                                usesColorScheme |= UsesPrefersColorSchemeMediaCondition(DecodeCssEscapes(value.ToString()));
                            }
                            else if (isSvgElement && IsCurrentColorPresentationAttribute(attributeName))
                            {
                                usesCurrentColor |= ContainsCssIdentifier(DecodeCssEscapes(value.ToString()), "currentColor");
                            }
                            if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                                attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                InspectElementReference(
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
                            if (MayContainCssResourceOrActiveContent(attributeName, value))
                            {
                                InspectCss(
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
                        ChargeStructuralPayload(
                            css,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted CSS ceiling.");
                        InspectCss(
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
                        ChargeStructuralPayload(
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
                throw Unsupported("The SVG is malformed.", exception);
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
                throw Unsupported("The SVG has no document root.");

            bool requiresUtf8Normalization;
            using (SvgSourceBuffer.ReadLease sourceLease = source.AcquireReadLease())
            {
                requiresUtf8Normalization = RequiresUtf8Normalization(
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

                    using BoundedOutputStream normalized = await NormalizeToUtf8SegmentsAsync(
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
                    throw Unsupported("The SVG could not be normalized from its declared source encoding.", exception);
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            // The retained ticket measures actual owned byte arrays. The
            // normalized tail was compacted before transfer, so this should be
            // the final source-byte capacity, not a logical-length estimate.
            ownership?.SetLength(finalSource.AllocatedCapacity);
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                if (ownership is not null)
                {
                    result.AttachOwnership(ownership);
                    ownership = null;
                }
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            ownership?.Dispose();
            ownership = null;
#endif
            snapshotAdmission?.Dispose();
            snapshotAdmission = null;
            demand = null;
            beforeFirstNormalizedSegmentAllocation = null;
        }
    }

    private static XmlReaderSettings CreateReaderSettings(
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

    private static bool RequiresUtf8Normalization(ReadOnlySpan<byte> source, string? declaredEncoding)
    {
        if (HasNonUtf8EncodingPrefix(source))
            return true;
        if (string.IsNullOrWhiteSpace(declaredEncoding))
            return false;

        try
        {
            return Encoding.GetEncoding(declaredEncoding).CodePage != Encoding.UTF8.CodePage;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // The XmlReader has already accepted and decoded the declaration;
            // if this process cannot name that encoding directly, normalize it
            // rather than handing the original bytes to the UTF-8 worker.
            return true;
        }
    }

    private static bool HasNonUtf8EncodingPrefix(ReadOnlySpan<byte> source)
    {
        if (source.StartsWith<byte>([0xef, 0xbb, 0xbf]))
            return false;
        if (source.StartsWith<byte>([0xff, 0xfe, 0x00, 0x00]) ||
            source.StartsWith<byte>([0x00, 0x00, 0xfe, 0xff]) ||
            source.StartsWith<byte>([0xff, 0xfe]) ||
            source.StartsWith<byte>([0xfe, 0xff]))
        {
            return true;
        }

        if (source.Length < 4)
            return false;

        byte first = source[0];
        byte second = source[1];
        byte third = source[2];
        byte fourth = source[3];
        // XML's initial markup bytes let XmlReader identify BOM-less UTF-16/32
        // documents; recognize those same NUL patterns, including leading XML
        // whitespace, before treating the source as the UTF-8 fast path.
        return (first == 0 && second == 0 && third == 0 && fourth != 0) ||
               (first != 0 && second == 0 && third == 0 && fourth == 0) ||
               (first == 0 && third == 0 && second != 0 && fourth != 0) ||
               (first != 0 && second == 0 && third != 0 && fourth == 0);
    }

    // Return only segmented output, never an input stream or source alias. The
    // caller owns the returned segments and must transfer or dispose them; a
    // cleanup failure still disposes untransferred output.
    internal static async Task<BoundedOutputStream> NormalizeToUtf8SegmentsAsync(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken,
        Action? beforeFirstSegmentAllocation = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using SvgSourceBuffer snapshot = SvgSourceBuffer.CopyFrom(source, cancellationToken);
        return await NormalizeToUtf8SegmentsAsync(
            snapshot,
            options,
            cancellationToken,
            beforeFirstSegmentAllocation).ConfigureAwait(false);
    }

    internal static async Task<BoundedOutputStream> NormalizeToUtf8SegmentsAsync(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken,
        Action? beforeFirstSegmentAllocation = null)
    {
        SvgSourceBuffer.ReadLease? inputLease = null;
        SvgSourceStream? input = null;
        XmlReader? reader = null;
        BoundedOutputStream? output = null;
        XmlWriter? writer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                inputLease = source.AcquireReadLease();
                input = new SvgSourceStream(inputLease, cancellationToken);
                inputLease = null;
                reader = XmlReader.Create(
                    input,
                    CreateReaderSettings(options.MaxSourceBytes, ignoreComments: false));
                output = new BoundedOutputStream(
                    options.MaxSourceBytes,
                    cancellationToken,
                    beforeFirstSegmentAllocation);
                beforeFirstSegmentAllocation = null;
                XmlWriterSettings writerSettings = new()
                {
                    Async = true,
                    CloseOutput = false,
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Indent = false,
                    NewLineHandling = NewLineHandling.None,
                    OmitXmlDeclaration = false,
                };
                writer = XmlWriter.Create(output, writerSettings);

                bool readNext = true;
                while (true)
                {
                    if (reader.EOF || reader.ReadState == ReadState.EndOfFile)
                        break;
                    if (readNext && !await reader.ReadAsync().ConfigureAwait(false))
                        break;
                    readNext = true;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (reader.NodeType == XmlNodeType.XmlDeclaration)
                    {
                        // WriteNode preserves the original declaration verbatim,
                        // including its non-UTF-8 encoding. Let the configured
                        // writer emit the matching declaration while preserving
                        // the authored standalone contract (XML 1.0 was validated).
                        switch (reader.GetAttribute("standalone"))
                        {
                            case "yes":
                                await writer.WriteStartDocumentAsync(standalone: true).ConfigureAwait(false);
                                break;
                            case "no":
                                await writer.WriteStartDocumentAsync(standalone: false).ConfigureAwait(false);
                                break;
                            default:
                                await writer.WriteStartDocumentAsync().ConfigureAwait(false);
                                break;
                        }
                        continue;
                    }

                    if (reader.Depth > options.MaxXmlDepth)
                        throw Resource("The SVG exceeds the XML-depth ceiling.");
                    if (reader.NodeType == XmlNodeType.DocumentType)
                    {
                        throw Unsupported("SVG DTDs and entities are forbidden.");
                    }
                    if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                        reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unsupported("External SVG stylesheets are forbidden.");
                    }

                    await writer.WriteNodeAsync(reader, defattr: true).ConfigureAwait(false);
                    // WriteNode consumes the current node and leaves the reader on
                    // its next sibling; that node must be processed before another
                    // ReadAsync advances the stream.
                    if (reader.EOF || reader.ReadState == ReadState.EndOfFile)
                        break;
                    readNext = false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await writer.FlushAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                try
                {
                    writer?.Dispose();
                }
                finally
                {
                    writer = null;
                    try
                    {
                        reader?.Dispose();
                    }
                    finally
                    {
                        reader = null;
                        try
                        {
                            input?.Dispose();
                        }
                        finally
                        {
                            input = null;
                            try
                            {
                                inputLease?.Dispose();
                            }
                            finally
                            {
                                inputLease = null;
                            }
                        }
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            BoundedOutputStream result = output;
            output = null;
            return result;
        }
        finally
        {
            output?.Dispose();
            output = null;
            beforeFirstSegmentAllocation = null;
        }
    }

    internal sealed class BoundedOutputStream(
        int maximumBytes,
        CancellationToken cancellationToken,
        Action? beforeFirstSegmentAllocation = null) : Stream
    {
        private List<byte[]>? _segments = [];
        private long _capacity;
        private long _length;
        private int _lastSegmentLength;
        private Action? _beforeFirstSegmentAllocation = beforeFirstSegmentAllocation;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => cancellationToken.ThrowIfCancellationRequested();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The write range is outside the buffer.");
            WriteCore(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

        public override void WriteByte(byte value)
        {
            Span<byte> singleByte = stackalloc byte[1];
            singleByte[0] = value;
            WriteCore(singleByte);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCore(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        internal int AllocatedCapacity => checked((int)_capacity);

        /// <summary>Transfers the writer's segment arrays without flattening or copying source bytes.</summary>
        internal SvgSourceBuffer DetachSourceBuffer()
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(BoundedOutputStream));

            // Keep tiny normalized snapshots from retaining nearly one chunk
            // of unused capacity apiece. Allocate and validate the compacted
            // tail before replacing the writer's owner entry so cancellation
            // or allocation failure leaves ordinary Dispose cleanup intact.
            if (_lastSegmentLength > 0 && _lastSegmentLength < segments[^1].Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] compactTail = GC.AllocateUninitializedArray<byte>(_lastSegmentLength);
                segments[^1].AsSpan(0, _lastSegmentLength).CopyTo(compactTail);
                cancellationToken.ThrowIfCancellationRequested();
                _capacity -= segments[^1].Length - compactTail.Length;
                segments[^1] = compactTail;
            }

            SvgSourceBuffer source = SvgSourceBuffer.TakeOwnedSegments(
                segments,
                checked((int)_length),
                checked((int)_capacity));
            _segments = null;
            _capacity = 0;
            _length = 0;
            _lastSegmentLength = 0;
            return source;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _segments?.Clear();
                _segments = null;
            }
            _capacity = 0;
            _length = 0;
            _lastSegmentLength = 0;
            _beforeFirstSegmentAllocation = null;
            base.Dispose(disposing);
        }

        private void WriteCore(ReadOnlySpan<byte> value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Length > maximumBytes - _length)
                throw Resource("The normalized SVG exceeds the configured source-byte ceiling.");

            while (!value.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(BoundedOutputStream));
                if (segments.Count == 0 || _lastSegmentLength == segments[^1].Length)
                {
                    int capacity = checked((int)Math.Min(SvgSourceBuffer.SegmentSize, maximumBytes - _capacity));
                    if (capacity <= 0)
                        throw Resource("The normalized SVG exceeds the configured source-byte ceiling.");
                    Action? allocationObserver = Interlocked.Exchange(
                        ref _beforeFirstSegmentAllocation,
                        null);
                    allocationObserver?.Invoke();
                    // Tests can deterministically cancel at the growth/allocation
                    // boundary; cancellation still wins before any segment exists.
                    cancellationToken.ThrowIfCancellationRequested();
                    segments.Add(GC.AllocateUninitializedArray<byte>(capacity));
                    _capacity += capacity;
                    _lastSegmentLength = 0;
                }

                byte[] segment = segments[^1];
                int copyLength = Math.Min(value.Length, segment.Length - _lastSegmentLength);
                value[..copyLength].CopyTo(segment.AsSpan(_lastSegmentLength));
                _lastSegmentLength += copyLength;
                _length += copyLength;
                value = value[copyLength..];
            }
        }
    }

    private static void InspectReference(
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        ResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        if (value.IsEmpty || value[0] == '#')
            return;
        if (!value.StartsWith("data:".AsSpan(), StringComparison.OrdinalIgnoreCase))
            throw Unsupported("Nested network and file references are forbidden.");

        int comma = value.IndexOf(',');
        if (comma <= 5)
            throw Unsupported("An embedded image data URI is malformed.");
        ReadOnlySpan<char> header = value[5..comma];
        int parameterStart = header.IndexOf(';');
        ReadOnlySpan<char> declaredMediaType = (parameterStart < 0 ? header : header[..parameterStart]).Trim();
        // Small references are cheaper to decode than to maintain in the memo.
        // A declared nested SVG is never a useful memo candidate, even if the
        // raster sniffer later discovers a mislabeled PNG. Always inspect it.
        // Hash the complete XML-decoded URI, including its header and encoding;
        // payload-only or sampled keys could bypass validation of another URI.
        bool memoizeRaster = value.Length >= 1024 &&
            !declaredMediaType.Equals("image/svg+xml".AsSpan(), StringComparison.OrdinalIgnoreCase);
        RasterReferenceKey referenceKey = default;
        if (memoizeRaster)
        {
            cancellationToken.ThrowIfCancellationRequested();
            referenceKey = RasterReferenceKey.Create(value);
            cancellationToken.ThrowIfCancellationRequested();
            if (resources.IsRasterValidated(referenceKey))
                return;
        }
        ReadOnlySpan<char> payload = value[(comma + 1)..];
        // Small embedded rasters need only signature/dimension/hash inspection
        // here; their actual decode belongs to the worker. Keep those transient
        // bytes on this bounded stack, including when the MIME label is wrong.
        // Nested XML still obtains its own array for the synchronous reader.
        Span<byte> smallPayload = stackalloc byte[512];
        Span<byte> target = smallPayload;
        byte[]? ownedPayload = null;
        scoped ReadOnlySpan<byte> decoded;
        if (header.Contains(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            int length = SvgDataUriDecoder.GetBase64DecodedLength(
                payload, options.MaxEmbeddedImageBytes, cancellationToken);
            target = length <= smallPayload.Length
                ? smallPayload[..length]
                : (ownedPayload = new byte[length]);
            SvgDataUriDecoder.DecodeBase64Into(payload, target, cancellationToken);
            decoded = target;
        }
        else
        {
            ownedPayload = SvgDataUriDecoder.DecodePercentEncoded(
                payload, options.MaxEmbeddedImageBytes, cancellationToken);
            decoded = ownedPayload;
        }
        string? mediaType = SniffEmbeddedImageMediaType(decoded);
        if (mediaType is null)
        {
            ownedPayload ??= decoded.ToArray();
            mediaType = NormalizeEmbeddedImageMediaType(
                declaredMediaType, ownedPayload, options, cancellationToken);
        }
        if (mediaType is null)
            throw Unsupported("The embedded image media type is unsupported.");

        // Keep the complete digest in the per-inspection value-key set. A
        // byte[] digest plus a 64-character hex string per image needlessly
        // expands transient host memory on contributor grids. This keeps the
        // exact same SHA-256 deduplication without retaining either payload.
        RasterReferenceKey identity = RasterReferenceKey.Create(decoded);
        bool isSvg = mediaType == "image/svg+xml";
        int nestedSvgDepth = embeddedSvgDepth + 1;
        if (isSvg && nestedSvgDepth >= options.MaxNestedSvgDepth)
        {
            // The configured maximum includes the original document, matching
            // the worker's top-level reference budget (max depth minus one).
            throw Resource("The SVG exceeds the nested-SVG ceiling.");
        }

        if (resources.AddResource(identity))
        {
            embeddedBytes = checked(embeddedBytes + decoded.Length);
            if (embeddedBytes > options.MaxEmbeddedImageBytes)
                throw Resource("Embedded images exceed the decoded-byte ceiling.");

            if (!isSvg)
            {
                if (!TryReadImageDimensions(decoded, mediaType, out int imageWidth, out int imageHeight))
                    throw Unsupported("An embedded raster image is malformed or has no bounded dimensions.");
                embeddedPixels = checked(embeddedPixels + checked((long)imageWidth * imageHeight));
                if (embeddedPixels > options.MaxEmbeddedImagePixels)
                    throw Resource("Embedded images exceed the decoded-pixel ceiling.");
            }
        }

        if (isSvg && !resources.IsNestedSvgValidatedAtDepth(identity, nestedSvgDepth))
        {
            InspectNestedSvgMetadata(
                ownedPayload!,
                options,
                resources,
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                nestedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme);
            resources.MarkNestedSvgValidated(identity, nestedSvgDepth);
        }
        if (!isSvg && memoizeRaster)
            resources.RememberRaster(referenceKey);
    }

    private static string? NormalizeEmbeddedImageMediaType(
        ReadOnlySpan<char> declaredMediaType,
        byte[] decoded,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        if (declaredMediaType.Equals("image/jpg".AsSpan(), StringComparison.OrdinalIgnoreCase))
            declaredMediaType = "image/jpeg";

        // The byte signature is authoritative. Several real contributor-card
        // generators label PNG avatars as image/gif; browsers sniff these and
        // render them, while trusting the declaration made our bounded dimension
        // parser reject otherwise valid content.
        string? sniffed = SniffEmbeddedImageMediaType(decoded);
        if (sniffed is not null)
            return sniffed;
        if (declaredMediaType.Equals("image/svg+xml".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
            LooksLikeSvg(decoded, options, cancellationToken))
        {
            return "image/svg+xml";
        }

        // Browsers tolerate missing and invalid data-URI media types when the
        // payload has an unambiguous raster signature. OpenCollective emits
        // contributor avatars as `data:false;base64,...`; accepting only
        // positively identified bounded raster formats preserves that useful
        // compatibility without turning arbitrary payloads into nested SVG.
        return null;
    }

    private static string? SniffEmbeddedImageMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            return "image/jpeg";
        if (data.Length >= 6 &&
            (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    private static bool LooksLikeSvg(
        byte[] data,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        if (HasNonUtf8EncodingPrefix(data))
            throw Unsupported("Nested SVG encodings other than UTF-8 are not supported.");

        using var stream = new MemoryStream(data, writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            CreateReaderSettings(
                Math.Min(options.MaxSourceBytes, options.MaxEmbeddedImageBytes),
                ignoreComments: true,
                asynchronous: false));
        string? declaredEncoding = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > options.MaxXmlDepth)
                throw Resource("The embedded SVG exceeds the XML-depth ceiling.");
            if (reader.NodeType == XmlNodeType.XmlDeclaration)
            {
                declaredEncoding = reader.GetAttribute("encoding");
                continue;
            }
            if (reader.NodeType == XmlNodeType.DocumentType)
            {
                throw Unsupported("Embedded SVG DTDs are forbidden.");
            }
            if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported("External SVG stylesheets are forbidden.");
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
            {
                return false;
            }
            if (RequiresUtf8Normalization(data, declaredEncoding))
                throw Unsupported("Nested SVG encodings other than UTF-8 are not supported.");
            // The worker remains authoritative for the complete nested tree.
            // Host sniffing needs only a bounded, secure prolog/root probe.
            return true;
        }

        return false;
    }

    private static void InspectNestedSvgMetadata(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        ResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        // The data URI was already decoded and charged by InspectReference. Read
        // that same byte[] directly so nested metadata inspection adds no source
        // copy or DOM-sized representation.
        using var stream = new MemoryStream(source, writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            CreateReaderSettings(
                Math.Min(options.MaxSourceBytes, options.MaxEmbeddedImageBytes),
                ignoreComments: true,
                asynchronous: false));

        bool sawRoot = false;
        bool readNext = true;
        int svgNesting = 0;
        char[]? attributeScratch = null;
        while (true)
        {
            if (readNext && !reader.Read())
                break;
            readNext = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > options.MaxXmlDepth)
                throw Resource("The embedded SVG exceeds the XML-depth ceiling.");

            if (reader.NodeType == XmlNodeType.DocumentType)
                throw Unsupported("Embedded SVG DTDs are forbidden.");
            if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported("External SVG stylesheets are forbidden.");
            }
            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
            {
                svgNesting--;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            string name = reader.LocalName;
            bool isSvgElement = reader.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal);
            if (!sawRoot)
            {
                if (!isSvgElement || !name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                    throw Unsupported("The embedded document root is not an SVG element.");
                sawRoot = true;
            }

            // OpenAsync is a public provider boundary, so its recursive walk
            // must enforce the same active-content policy as the outer SVG.
            // ImageBox normally performs an earlier admission pass, but a
            // caller may invoke the provider directly with nested SVG bytes.
            if (ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw Unsupported($"The static SVG subset does not allow <{name}>.");

            if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                svgNesting++;
                if (svgNesting > options.MaxNestedSvgDepth)
                    throw Resource("The SVG exceeds the nested-SVG ceiling.");
            }
            // The worker's text metadata is local-name based, including foreign
            // namespace descendants encountered while inspecting nested bytes.
            hasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("textPath", StringComparison.OrdinalIgnoreCase);

            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string attributeName = reader.LocalName;
                    ReadOnlySpan<char> value = ReadAttributeValue(
                        reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken).Trim();
                    if (attributeName.Length > 2 &&
                        attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unsupported("SVG event handlers are forbidden.");
                    }

                    if (isSvgElement && attributeName.Equals("media", StringComparison.OrdinalIgnoreCase))
                    {
                        usesColorScheme |= UsesPrefersColorSchemeMediaCondition(DecodeCssEscapes(value.ToString()));
                    }
                    else if (isSvgElement && IsCurrentColorPresentationAttribute(attributeName))
                    {
                        usesCurrentColor |= ContainsCssIdentifier(DecodeCssEscapes(value.ToString()), "currentColor");
                    }

                    if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                        attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        InspectElementReference(
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
                    if (MayContainCssResourceOrActiveContent(attributeName, value))
                    {
                        InspectCss(
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

            if (name.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                !reader.IsEmptyElement)
            {
                string css = reader.ReadElementContentAsString();
                InspectCss(
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
                // ReadElementContentAsString advances to the first node after the
                // element; process it before asking the reader to advance again.
                readNext = false;
                continue;
            }

            if (reader.IsEmptyElement && isSvgElement &&
                name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                svgNesting--;
            }
        }

        if (!sawRoot)
            throw Unsupported("The embedded SVG has no document root.");
    }

    private static void InspectElementReference(
        string elementName,
        string attributeName,
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        ResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        // An SVG rendered as an image has no navigation surface. Keep authored
        // hyperlinks for fidelity, but do not treat their inert destinations as
        // subresources. Resource-bearing elements remain data-URI-only.
        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) &&
            elementName.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (value.Length == 0 || value[0] == '#')
            return;

        bool isEmbeddedImage =
            elementName.Equals("image", StringComparison.OrdinalIgnoreCase) ||
            elementName.Equals("feImage", StringComparison.OrdinalIgnoreCase);
        if (!isEmbeddedImage)
            throw Unsupported("Nested network and file references are forbidden.");

        InspectReference(
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

    private static void ChargeStructuralPayload(
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        if (value.Length == 0)
            return;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);
        int payloadCost = utf8Bytes / 64;
        if (payloadCost == 0)
            return;
        ChargeStructural(
            payloadCost,
            options,
            ref structuralCost,
            message);
    }

    private static ReadOnlySpan<char> ReadAttributeValue(
        XmlReader reader,
        ref char[]? scratch,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        // XmlReader keeps ordinary attribute characters in its parse buffer.
        // Reuse a bounded scratch buffer instead of materializing a string for
        // every geometry/data-URI attribute in dense contributor grids. Every
        // value still goes through the same structural, reference and CSS
        // checks. The span is consumed before this buffer can be reused.
        // The reader already enforces the document character ceiling. Grow
        // only to the largest attribute actually encountered, reuse it for the
        // rest of this inspection, and release it before normalization awaits.
        // Large repeated data URIs must not allocate a StringBuilder plus a
        // flattened string for every reference. The extra two characters let
        // the final read detect EOF without splitting a UTF-16 surrogate pair.
        int maximumCapacity = checked(maximumCharacters + 2);
        scratch ??= new char[Math.Min(1026, maximumCapacity)];
        int length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scratch.Length - length < 2)
            {
                int capacity = Math.Min(checked(scratch.Length * 2), maximumCapacity);
                if (capacity <= scratch.Length)
                    throw Resource("The SVG attribute exceeds the source-character ceiling.");
                Array.Resize(ref scratch, capacity);
            }
            // Keep cancellation checks frequent even after a large data URI has
            // expanded the reusable destination buffer. Two or more characters
            // remain available so the reader need not split a surrogate pair.
            int count = reader.ReadValueChunk(scratch, length, Math.Min(4096, scratch.Length - length));
            if (count == 0)
                return scratch.AsSpan(0, length);
            length = checked(length + count);
        }
    }

    private static void ChargeStructural(
        int amount,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        long next = (long)structuralCost + amount;
        if (next > options.MaxStructuralCost)
            throw Resource(message);
        structuralCost = checked((int)next);
    }

    private static int ComputePathComplexity(ReadOnlySpan<char> pathData)
    {
        long commands = 0;
        long numbers = 0;
        ReadOnlySpan<char> path = pathData;
        int cursor = 0;
        while (cursor < path.Length)
        {
            char current = path[cursor];
            if ("MmZzLlHhVvCcSsQqTtAa".Contains(current))
            {
                commands++;
                cursor++;
                continue;
            }

            int start = cursor;
            if (current is '+' or '-')
                cursor++;
            bool hasDigits = false;
            while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
            {
                hasDigits = true;
                cursor++;
            }
            if (cursor < path.Length && path[cursor] == '.')
            {
                cursor++;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                {
                    hasDigits = true;
                    cursor++;
                }
            }
            if (!hasDigits)
            {
                cursor = start + 1;
                continue;
            }
            if (cursor < path.Length && path[cursor] is 'e' or 'E')
            {
                int exponent = cursor++;
                if (cursor < path.Length && path[cursor] is '+' or '-')
                    cursor++;
                int exponentDigits = cursor;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                    cursor++;
                if (cursor == exponentDigits)
                    cursor = exponent;
            }
            numbers++;
        }

        long payload = (Encoding.UTF8.GetByteCount(pathData) + 63L) / 64L;
        // Commands and coordinates are much cheaper than independent DOM
        // elements in resvg. Keep a weighted charge so adversarial paths remain
        // bounded, but do not price a legitimate chart or outlined-font path as
        // hundreds of thousands of elements. The 8 MiB source ceiling and the
        // payload charge still reject a maximally dense path before admission.
        long cost = ((commands + 7) / 8) + ((numbers + 15) / 16) + payload;
        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    private static bool MayContainCssResourceOrActiveContent(string attributeName, ReadOnlySpan<char> value)
    {
        if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase))
            return true;

        ReadOnlySpan<char> normalized = value.Contains('\\') ? DecodeCssEscapes(value.ToString()).AsSpan() : value;
        if (normalized.Contains("url", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("transition", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsCurrentColorPresentationAttribute(string attributeName) =>
        CurrentColorPresentationAttributes.Contains(attributeName);

    private static bool ContainsCssIdentifier(string css, string identifier)
        => ContainsCssIdentifier(css.AsSpan(), identifier);

    private static bool ContainsCssIdentifier(ReadOnlySpan<char> css, string identifier)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                {
                    if (css[cursor] == '\\' && cursor + 1 < css.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < css.Length)
                    cursor++;
                continue;
            }
            if (!IsCssIdentifierCharacter(css[cursor]))
            {
                cursor++;
                continue;
            }

            int start = cursor++;
            while (cursor < css.Length && IsCssIdentifierCharacter(css[cursor]))
                cursor++;
            ReadOnlySpan<char> token = css.Slice(start, cursor - start);
            if (token.Equals("url".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                int functionStart = cursor;
                if (TrySkipCssUrlFunction(css, ref functionStart))
                {
                    cursor = functionStart;
                    continue;
                }
            }
            if (token.Equals(identifier.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TrySkipCssUrlFunction(ReadOnlySpan<char> css, ref int cursor)
    {
        int candidate = cursor;
        while (candidate < css.Length && char.IsWhiteSpace(css[candidate]))
            candidate++;
        if (candidate + 1 < css.Length && css[candidate] == '/' && css[candidate + 1] == '*')
        {
            int commentEnd = css[(candidate + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
            if (commentEnd < 0)
                return false;
            candidate += commentEnd + 4;
            while (candidate < css.Length && char.IsWhiteSpace(css[candidate]))
                candidate++;
        }
        if (candidate >= css.Length || css[candidate] != '(')
            return false;

        int depth = 1;
        cursor = candidate + 1;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                {
                    if (css[cursor] == '\\' && cursor + 1 < css.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < css.Length)
                    cursor++;
                continue;
            }
            if (css[cursor] == '(')
                depth++;
            else if (css[cursor] == ')' && --depth == 0)
            {
                cursor++;
                return true;
            }
            cursor++;
        }
        return false;
    }

    private static bool IsCssIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' || value >= 0x80;

    private static bool UsesCurrentColorInCss(string css, bool isDeclarationList)
    {
        int cursor = 0;
        int declarationStart = 0;
        int blockDepth = 0;
        int parentheses = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;

            char current = css[cursor];
            if (current == '(')
            {
                parentheses++;
            }
            else if (current == ')' && parentheses > 0)
            {
                parentheses--;
            }
            else if (parentheses == 0 && current == '{')
            {
                blockDepth++;
                declarationStart = cursor + 1;
            }
            else if (parentheses == 0 && current == ';')
            {
                if ((blockDepth > 0 || isDeclarationList) &&
                    DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart, cursor - declarationStart)))
                {
                    return true;
                }
                declarationStart = cursor + 1;
            }
            else if (parentheses == 0 && current == '}')
            {
                if (blockDepth > 0 &&
                    DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart, cursor - declarationStart)))
                {
                    return true;
                }
                if (blockDepth > 0)
                    blockDepth--;
                declarationStart = cursor + 1;
            }
            cursor++;
        }

        return (blockDepth > 0 || isDeclarationList) &&
            DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart));
    }

    private static bool DeclarationValueUsesCurrentColor(ReadOnlySpan<char> declaration)
    {
        int parentheses = 0;
        int cursor = 0;
        while (cursor < declaration.Length)
        {
            if (cursor + 1 < declaration.Length && declaration[cursor] == '/' && declaration[cursor + 1] == '*')
            {
                int commentEnd = declaration[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (declaration[cursor] is '\'' or '"')
            {
                char quote = declaration[cursor++];
                while (cursor < declaration.Length && declaration[cursor] != quote)
                {
                    if (declaration[cursor] == '\\' && cursor + 1 < declaration.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < declaration.Length)
                    cursor++;
                continue;
            }

            if (declaration[cursor] == '(')
                parentheses++;
            else if (declaration[cursor] == ')' && parentheses > 0)
                parentheses--;
            else if (declaration[cursor] == ':' && parentheses == 0)
            {
                ReadOnlySpan<char> property = declaration[..cursor].Trim();
                if (!property.IsEmpty && property[0] != '@')
                    return ContainsCssIdentifier(declaration[(cursor + 1)..], "currentColor");
                return false;
            }
            cursor++;
        }

        return false;
    }

    private static bool UsesPrefersColorSchemeInCss(string css)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;
            if (!css.AsSpan(cursor).StartsWith("@media", StringComparison.OrdinalIgnoreCase) ||
                (cursor + 6 < css.Length && IsCssIdentifierCharacter(css[cursor + 6])))
            {
                cursor++;
                continue;
            }

            int conditionStart = cursor + 6;
            int conditionEnd = conditionStart;
            int parentheses = 0;
            while (conditionEnd < css.Length)
            {
                if (TrySkipCssComment(css, ref conditionEnd) || TrySkipCssString(css, ref conditionEnd))
                    continue;
                if (css[conditionEnd] == '(')
                    parentheses++;
                else if (css[conditionEnd] == ')' && parentheses > 0)
                    parentheses--;
                else if (css[conditionEnd] == '{' && parentheses == 0)
                    break;
                conditionEnd++;
            }

            if (ContainsCssIdentifier(css[conditionStart..conditionEnd], "prefers-color-scheme"))
                return true;
            cursor = conditionEnd < css.Length ? conditionEnd + 1 : conditionEnd;
        }

        return false;
    }

    private static bool UsesPrefersColorSchemeMediaCondition(string value) =>
        ContainsCssIdentifier(value, "prefers-color-scheme");

    private static string RemoveInertNamespaceDeclarations(string css)
    {
        char[]? sanitized = null;
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;
            if (!css.AsSpan(cursor).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            cursor += "@namespace".Length;
            int parentheses = 0;
            char quote = '\0';
            bool terminated = false;
            while (cursor < css.Length)
            {
                char current = css[cursor];
                if (quote != '\0')
                {
                    if (current == '\\' && cursor + 1 < css.Length)
                        cursor += 2;
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        cursor++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                {
                    quote = current;
                    cursor++;
                    continue;
                }
                if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (current == '{' && parentheses == 0)
                    throw Unsupported("A CSS namespace declaration is malformed.");
                else if (current == ';' && parentheses == 0)
                {
                    cursor++;
                    terminated = true;
                    break;
                }
                cursor++;
            }
            if (!terminated)
                throw Unsupported("A CSS namespace declaration is not terminated.");
            sanitized ??= css.ToCharArray();
            Array.Fill(sanitized, ' ', start, cursor - start);
        }
        return sanitized is null ? css : new string(sanitized);
    }

    private static void InspectCss(
        string css,
        ResvgMarkdownSvgRendererOptions options,
        ResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme,
        bool isDeclarationList = false,
        bool trackThemeMetadata = true)
    {
        string normalized = DecodeCssEscapes(css);
        if (trackThemeMetadata)
        {
            usesCurrentColor |= UsesCurrentColorInCss(normalized, isDeclarationList);
            usesColorScheme |= UsesPrefersColorSchemeInCss(normalized);
        }
        if (normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            ContainsMotionDeclaration(normalized))
        {
            throw Unsupported("External fonts, imports, and animation are forbidden.");
        }

        normalized = RemoveInertNamespaceDeclarations(normalized);
        int cursor = 0;
        while (cursor < normalized.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySkipCssComment(normalized, ref cursor) || TrySkipCssString(normalized, ref cursor))
                continue;
            if (!normalized.AsSpan(cursor).StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int afterName = cursor + 3;
            SkipCssWhitespaceAndComments(normalized, ref afterName);
            if (afterName >= normalized.Length || normalized[afterName] != '(')
            {
                cursor++;
                continue;
            }

            cursor = afterName + 1;
            SkipCssWhitespaceAndComments(normalized, ref cursor);
            ReadOnlySpan<char> target;
            if (cursor < normalized.Length && normalized[cursor] is '\'' or '"')
            {
                char quote = normalized[cursor++];
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != quote)
                    cursor++;
                if (cursor >= normalized.Length)
                    throw Unsupported("A quoted CSS URL is not terminated.");
                target = normalized.AsSpan(start, cursor - start);
                cursor++;
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw Unsupported("A quoted CSS URL has invalid trailing content.");
            }
            else
            {
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != ')' &&
                       !char.IsWhiteSpace(normalized[cursor]))
                {
                    cursor++;
                }
                target = normalized.AsSpan(start, cursor - start);
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw Unsupported("An unquoted CSS URL is malformed.");
            }
            cursor++;
            target = target.Trim();
            if (!target.IsEmpty && target[0] == '#')
                continue;
            InspectReference(
                target,
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
    }

    private static bool ContainsMotionDeclaration(string css)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetterOrDigit(css[cursor]) || css[cursor] is '-' or '_'))
            {
                cursor++;
            }
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            string property = css[nameStart..cursor];
            int separator = cursor;
            while (separator < css.Length && char.IsWhiteSpace(css[separator]))
                separator++;
            if (separator >= css.Length || css[separator] != ':' || !IsMotionProperty(property))
                continue;

            int end = separator + 1;
            char quote = '\0';
            int parentheses = 0;
            while (end < css.Length)
            {
                char current = css[end];
                if (quote != '\0')
                {
                    if (current == '\\' && end + 1 < css.Length)
                        end += 2;
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        end++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                    quote = current;
                else if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (parentheses == 0 && current == '{')
                    break;
                else if (parentheses == 0 && current is (';' or '}'))
                    return true;
                end++;
            }

            if (end >= css.Length)
                return true;
            cursor = end + 1;
        }

        return false;
    }

    private static bool IsMotionProperty(string property)
        => property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-transition-", StringComparison.OrdinalIgnoreCase);

    private static string DecodeCssEscapes(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var output = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                output.Append(current);
                continue;
            }
            if (++index >= value.Length)
                throw Unsupported("A CSS escape is incomplete.");
            if (value[index] is '\r' or '\n' or '\f')
            {
                if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
                continue;
            }

            int scalar = 0;
            int digits = 0;
            while (index < value.Length && digits < 6 && Uri.IsHexDigit(value[index]))
            {
                scalar = checked((scalar * 16) + HexValue(value[index]));
                digits++;
                index++;
            }
            if (digits != 0)
            {
                if (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                        index++;
                }
                else
                {
                    index--;
                }
                if (!Rune.IsValid(scalar) || scalar == 0)
                    throw Unsupported("A CSS escape contains an invalid scalar value.");
                output.Append(char.ConvertFromUtf32(scalar));
            }
            else
            {
                output.Append(value[index]);
            }
        }
        return output.ToString();
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void SkipCssWhitespaceAndComments(string css, ref int cursor)
    {
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (TrySkipCssComment(css, ref cursor))
                continue;
            break;
        }
    }

    private static bool TrySkipCssComment(string css, ref int cursor)
    {
        if (cursor + 1 >= css.Length || css[cursor] != '/' || css[cursor + 1] != '*')
            return false;
        int end = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
        if (end < 0)
            throw Unsupported("A CSS comment is not terminated.");
        cursor = end + 2;
        return true;
    }

    private static bool TrySkipCssString(string css, ref int cursor)
    {
        if (css[cursor] is not ('\'' or '"'))
            return false;
        char quote = css[cursor++];
        while (cursor < css.Length && css[cursor] != quote)
            cursor++;
        if (cursor >= css.Length)
            throw Unsupported("A CSS string is not terminated.");
        cursor++;
        return true;
    }

    private static double? ParseCssLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        double scale = 1;
        string number = value;
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            number = value[..^2];
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 72d;
        }
        else if (value.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96;
        }
        else if (value.EndsWith("cm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 2.54d;
        }
        else if (value.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 25.4d;
        }
        else if (value.EndsWith('%') || value.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                 value.EndsWith("ex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) && parsed > 0
            ? parsed * scale
            : null;
    }

    private static bool TryReadImageDimensions(ReadOnlySpan<byte> data, string mediaType, out int width, out int height)
    {
        width = height = 0;
        if (mediaType == "image/png" && data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
        {
            width = ReadBigEndianInt32(data[16..20]);
            height = ReadBigEndianInt32(data[20..24]);
            return width > 0 && height > 0;
        }
        if (mediaType == "image/gif" && data.Length >= 10 &&
            (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            width = data[6] | data[7] << 8;
            height = data[8] | data[9] << 8;
            return width > 0 && height > 0;
        }
        if (mediaType == "image/webp" && data.Length >= 25 &&
            data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            if (data[12..16].SequenceEqual("VP8X"u8) && data.Length >= 30)
            {
                width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
                height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
                return width > 0 && height > 0;
            }
            if (data[12..16].SequenceEqual("VP8 "u8) && data.Length >= 30 &&
                data[23] == 0x9d && data[24] == 0x01 && data[25] == 0x2a)
            {
                width = (data[26] | data[27] << 8) & 0x3fff;
                height = (data[28] | data[29] << 8) & 0x3fff;
                return width > 0 && height > 0;
            }
            if (data[12..16].SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                width = 1 + data[21] + ((data[22] & 0x3f) << 8);
                height = 1 + (data[22] >> 6) + (data[23] << 2) + ((data[24] & 0x0f) << 10);
                return width > 0 && height > 0;
            }
            return false;
        }
        if (mediaType == "image/jpeg" && data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            int index = 2;
            while (index + 8 < data.Length)
            {
                if (data[index] != 0xff)
                {
                    index++;
                    continue;
                }
                byte marker = data[index + 1];
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    height = (data[index + 5] << 8) | data[index + 6];
                    width = (data[index + 7] << 8) | data[index + 8];
                    return width > 0 && height > 0;
                }
                int segment = (data[index + 2] << 8) | data[index + 3];
                if (segment < 2)
                    return false;
                index += segment + 2;
            }
        }
        return false;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static MarkdownSvgException Unsupported(string message, Exception? inner = null) =>
        new(MarkdownSvgFailureReason.UnsupportedContent, message, inner);

    private static MarkdownSvgException Resource(string message) =>
        new(MarkdownSvgFailureReason.ResourceLimitExceeded, message);
}
