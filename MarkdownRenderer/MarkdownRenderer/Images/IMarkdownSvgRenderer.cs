using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Opens sanitized, self-contained SVG content for metadata inspection and
/// subsequent rasterization.
/// </summary>
/// <remarks>
/// Implementations must be safe to share between controls and to call
/// concurrently. They must honor cancellation promptly and enforce their own
/// immutable active-operation deadlines; host queue time is deliberately not
/// charged to an active-render deadline. A control borrows this service and
/// never disposes it.
/// </remarks>
public interface IMarkdownSvgRenderer
{
    /// <summary>
    /// Produces inert, bounded SVG bytes for host metadata inspection and
    /// provider opening. Implementations must reject active content, external
    /// references, and over-budget input before returning. This runs off the
    /// UI thread; the provider must still repeat its authoritative checks.
    /// </summary>
    MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a monotonic token that changes when environment state can alter a
    /// rendered result, such as the installed-font generation.
    /// </summary>
    long CacheGeneration => 0;

    /// <summary>
    /// Raised after <see cref="CacheGeneration"/> changes so hosts can discard
    /// affected GPU rasters and schedule replacement renders.
    /// </summary>
    event EventHandler? CacheInvalidated
    {
        add { }
        remove { }
    }

    /// <summary>Opens one sanitized SVG document.</summary>
    /// <param name="request">Immutable source and rendering-environment inputs.</param>
    /// <param name="cancellationToken">Cancels the open operation.</param>
    /// <returns>A disposable document handle owned by the caller.</returns>
    /// <exception cref="MarkdownSvgException">The SVG cannot be opened within the provider's supported policy.</exception>
    ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inert SVG bytes or a typed host-preflight rejection.</summary>
public sealed class MarkdownSvgSourcePreparation
{
    private MarkdownSvgSourcePreparation(
        byte[]? sanitizedBytes,
        MarkdownSvgFailureReason? failureReason,
        string? failureDescription)
    {
        SanitizedBytes = sanitizedBytes;
        FailureReason = failureReason;
        FailureDescription = failureDescription;
    }

    /// <summary>Gets the admitted static source, or null for a rejection.</summary>
    public byte[]? SanitizedBytes { get; }

    /// <summary>Gets the typed reason for a rejection.</summary>
    public MarkdownSvgFailureReason? FailureReason { get; }

    /// <summary>Gets a safe, human-readable rejection description.</summary>
    public string? FailureDescription { get; }

    /// <summary>Creates an admitted static source.</summary>
    public static MarkdownSvgSourcePreparation Admit(byte[] sanitizedBytes)
    {
        ArgumentNullException.ThrowIfNull(sanitizedBytes);
        return new MarkdownSvgSourcePreparation(sanitizedBytes, null, null);
    }

    /// <summary>Creates a deterministic rejection without throwing.</summary>
    public static MarkdownSvgSourcePreparation Reject(
        MarkdownSvgFailureReason reason,
        string description)
    {
        if (reason is not (MarkdownSvgFailureReason.UnsupportedContent or
            MarkdownSvgFailureReason.ResourceLimitExceeded))
            throw new ArgumentOutOfRangeException(nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new MarkdownSvgSourcePreparation(null, reason, description);
    }
}

/// <summary>
/// An opened SVG document whose parsed state may be reused for more than one
/// raster size or tile.
/// </summary>
/// <remarks>
/// Implementations must support overlapping <see cref="RenderAsync"/> calls.
/// <see cref="IDisposable.Dispose"/> must be idempotent and may race admitted
/// renders; those renders must either complete normally or fail with
/// <see cref="MarkdownSvgFailureReason.Canceled"/>. Calls admitted after
/// disposal must fail with that same typed cancellation reason.
/// </remarks>
public interface IMarkdownSvgDocument : IDisposable
{
    /// <summary>Gets immutable metadata discovered while opening the document.</summary>
    MarkdownSvgDocumentInfo Info { get; }

    /// <summary>Rasterizes the full output or one physical-pixel tile.</summary>
    /// <param name="request">Requested full output dimensions, tile, and pixel format.</param>
    /// <param name="cancellationToken">Cancels the render operation.</param>
    /// <returns>An owned raster that must remain alive until upload completes.</returns>
    /// <exception cref="MarkdownSvgException">The render failed for a typed provider reason.</exception>
    ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Immutable inputs used to open one sanitized SVG.</summary>
/// <param name="SanitizedSvgBytes">
/// Sanitized SVG bytes admitted by the host. Providers must repeat all
/// authoritative security and resource checks rather than trusting preflight,
/// and must snapshot the bytes before <c>OpenAsync</c> returns. The caller may
/// release or reuse the backing memory after that point.
/// </param>
/// <param name="Locale">Optional BCP-47 locale used for SVG text and generic-family fallback.</param>
/// <param name="ColorScheme">Resolved light or dark color scheme.</param>
/// <param name="SemanticColor">Optional semantic color used to resolve <c>currentColor</c>.</param>
/// <param name="Priority">Scheduling priority for the open operation.</param>
public sealed record MarkdownSvgOpenRequest(
    ReadOnlyMemory<byte> SanitizedSvgBytes,
    string? Locale = null,
    MarkdownSvgColorScheme ColorScheme = MarkdownSvgColorScheme.Light,
    MarkdownSvgColor? SemanticColor = null,
    MarkdownSvgRenderPriority Priority = MarkdownSvgRenderPriority.Visible);

/// <summary>Immutable physical-pixel inputs for one SVG rasterization.</summary>
/// <param name="TargetWidthPixels">Width of the complete intended output in physical pixels.</param>
/// <param name="TargetHeightPixels">Height of the complete intended output in physical pixels.</param>
/// <param name="TileRegion">
/// Optional subregion to rasterize in complete-output coordinates. A null value
/// requests the complete output.
/// </param>
/// <param name="PixelFormat">Requested premultiplied output byte order.</param>
/// <param name="Priority">Scheduling priority for the raster operation.</param>
public sealed record MarkdownSvgRenderRequest(
    int TargetWidthPixels,
    int TargetHeightPixels,
    MarkdownSvgTileRegion? TileRegion = null,
    MarkdownSvgPixelFormat PixelFormat = MarkdownSvgPixelFormat.Rgba8Premultiplied,
    MarkdownSvgRenderPriority Priority = MarkdownSvgRenderPriority.Visible);

/// <summary>Host scheduling class for work that has not entered the worker.</summary>
public enum MarkdownSvgRenderPriority
{
    /// <summary>Content in the overscan region that may wait behind visible work.</summary>
    Overscan,

    /// <summary>Content intersecting the viewport or otherwise needed immediately.</summary>
    Visible,
}

/// <summary>A rectangular physical-pixel tile in complete-output coordinates.</summary>
/// <param name="X">Horizontal offset from the complete output's left edge.</param>
/// <param name="Y">Vertical offset from the complete output's top edge.</param>
/// <param name="Width">Tile width in physical pixels.</param>
/// <param name="Height">Tile height in physical pixels.</param>
public readonly record struct MarkdownSvgTileRegion(int X, int Y, int Width, int Height);

/// <summary>An engine-neutral, non-premultiplied semantic color value.</summary>
/// <param name="Red">Red channel.</param>
/// <param name="Green">Green channel.</param>
/// <param name="Blue">Blue channel.</param>
/// <param name="Alpha">Alpha channel.</param>
public readonly record struct MarkdownSvgColor(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha = byte.MaxValue);

/// <summary>Describes the color-scheme branch selected for static SVG CSS.</summary>
public enum MarkdownSvgColorScheme
{
    /// <summary>Resolve supported color-scheme rules using the light branch.</summary>
    Light,

    /// <summary>Resolve supported color-scheme rules using the dark branch.</summary>
    Dark,
}

/// <summary>Supported premultiplied 32-bit raster byte orders.</summary>
public enum MarkdownSvgPixelFormat
{
    /// <summary>Premultiplied red, green, blue, alpha bytes.</summary>
    Rgba8Premultiplied,

    /// <summary>Premultiplied blue, green, red, alpha bytes.</summary>
    Bgra8Premultiplied,
}

/// <summary>Metadata and dependency hints discovered while opening an SVG.</summary>
/// <param name="IntrinsicWidthDips">Optional intrinsic width in 96-DPI CSS pixels/DIPs.</param>
/// <param name="IntrinsicHeightDips">Optional intrinsic height in 96-DPI CSS pixels/DIPs.</param>
/// <param name="IntrinsicAspectRatio">Optional intrinsic width-to-height ratio.</param>
/// <param name="Description">Optional accessible description from the root <c>desc</c> element.</param>
/// <param name="HasText">True when rendering depends on the worker's font generation.</param>
/// <param name="UsesCurrentColor">True when rendering depends on the semantic color.</param>
/// <param name="UsesColorScheme">True when rendering depends on the selected color scheme.</param>
public sealed record MarkdownSvgDocumentInfo(
    double? IntrinsicWidthDips = null,
    double? IntrinsicHeightDips = null,
    double? IntrinsicAspectRatio = null,
    string? Description = null,
    bool HasText = false,
    bool UsesCurrentColor = false,
    bool UsesColorScheme = false);

/// <summary>
/// Owns a premultiplied raster buffer until a host completes GPU upload.
/// </summary>
public sealed class MarkdownSvgRaster : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly int _lengthBytes;

    /// <summary>Initializes an owned raster over provider-supplied memory.</summary>
    /// <param name="owner">Owner released by <see cref="Dispose"/>.</param>
    /// <param name="lengthBytes">Number of meaningful bytes at the start of the owner's memory.</param>
    /// <param name="widthPixels">Raster width in physical pixels.</param>
    /// <param name="heightPixels">Raster height in physical pixels.</param>
    /// <param name="strideBytes">Byte distance between adjacent raster rows.</param>
    /// <param name="pixelFormat">Premultiplied byte order.</param>
    public MarkdownSvgRaster(
        IMemoryOwner<byte> owner,
        int lengthBytes,
        int widthPixels,
        int heightPixels,
        int strideBytes,
        MarkdownSvgPixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strideBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        int minimumStride = checked(widthPixels * 4);
        if (strideBytes < minimumStride)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));

        int minimumLength = checked(strideBytes * heightPixels);
        if (lengthBytes < minimumLength || lengthBytes > owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(lengthBytes));

        if (!Enum.IsDefined(pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));

        _owner = owner;
        _lengthBytes = lengthBytes;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
    }

    /// <summary>Gets the meaningful raster bytes.</summary>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            IMemoryOwner<byte> owner = _owner ??
                throw new ObjectDisposedException(nameof(MarkdownSvgRaster));
            return owner.Memory[.._lengthBytes];
        }
    }

    /// <summary>Gets the number of meaningful raster bytes.</summary>
    public int LengthBytes => _lengthBytes;

    /// <summary>Gets the raster width in physical pixels.</summary>
    public int WidthPixels { get; }

    /// <summary>Gets the raster height in physical pixels.</summary>
    public int HeightPixels { get; }

    /// <summary>Gets the byte distance between adjacent raster rows.</summary>
    public int StrideBytes { get; }

    /// <summary>Gets the premultiplied raster byte order.</summary>
    public MarkdownSvgPixelFormat PixelFormat { get; }

    /// <summary>Releases the provider-owned raster memory.</summary>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

/// <summary>Identifies a safe, actionable SVG-provider failure category.</summary>
public enum MarkdownSvgFailureReason
{
    /// <summary>The document contains syntax outside the supported static subset.</summary>
    UnsupportedContent,

    /// <summary>A source, structure, decoded-resource, raster, or intermediate limit was exceeded.</summary>
    ResourceLimitExceeded,

    /// <summary>The hard operation deadline elapsed.</summary>
    Timeout,

    /// <summary>The isolated worker failed, crashed, or returned an invalid response.</summary>
    WorkerFailure,

    /// <summary>No compatible worker exists for the current process architecture.</summary>
    ArchitectureMismatch,

    /// <summary>The request was canceled before a safe result could be returned.</summary>
    Canceled,
}

/// <summary>An SVG-provider failure with a stable host-facing reason.</summary>
public sealed class MarkdownSvgException : Exception
{
    /// <summary>Initializes a typed SVG-provider failure.</summary>
    public MarkdownSvgException(
        MarkdownSvgFailureReason reason,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? GetDefaultMessage(reason), innerException)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        Reason = reason;
    }

    /// <summary>Gets the stable failure reason suitable for fallback selection.</summary>
    public MarkdownSvgFailureReason Reason { get; }

    private static string GetDefaultMessage(MarkdownSvgFailureReason reason) => reason switch
    {
        MarkdownSvgFailureReason.UnsupportedContent => "The SVG contains unsupported content.",
        MarkdownSvgFailureReason.ResourceLimitExceeded => "The SVG exceeds a resource limit.",
        MarkdownSvgFailureReason.Timeout => "The SVG operation timed out.",
        MarkdownSvgFailureReason.WorkerFailure => "The SVG worker failed.",
        MarkdownSvgFailureReason.ArchitectureMismatch => "The SVG worker does not support this architecture.",
        MarkdownSvgFailureReason.Canceled => "The SVG operation was canceled.",
        _ => "The SVG operation failed.",
    };
}
