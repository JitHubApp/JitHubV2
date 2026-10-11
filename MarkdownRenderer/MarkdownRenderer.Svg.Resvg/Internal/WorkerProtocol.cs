using System.Buffers.Binary;
using System.Text;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal enum WorkerOperation : ushort
{
    Hello = 1,
    Open = 2,
    Render = 3,
    TrimCache = 4,
    CloseDocument = 5,
    FontCatalog = 6,
}

internal enum WorkerStatus : ushort
{
    Ok = 0,
    Unsupported = 1,
    ResourceLimit = 2,
    WorkerFailure = 3,
    DocumentMissing = 4,
    FontCatalogPending = 5,
    FontCatalogFailure = 6,
}

internal readonly record struct WorkerRequest(
    WorkerOperation Operation,
    ulong RequestId,
    ulong NonceLow,
    ulong NonceHigh,
    ulong MappingHandle,
    byte[] ContentHash,
    int SourceLength,
    int OutputLength,
    int TargetWidth,
    int TargetHeight,
    MarkdownSvgTileRegion? Tile,
    string Locale,
    MarkdownSvgColorScheme ColorScheme,
    MarkdownSvgColor? SemanticColor,
    MarkdownSvgPixelFormat PixelFormat,
    long FontGeneration,
    long ParsedResourceCacheBytes,
    ulong DocumentId,
    ResvgMarkdownSvgRendererOptions Options,
    MarkdownSvgPhysicalViewport? PhysicalViewport = null);

internal readonly record struct WorkerResponse(
    WorkerStatus Status,
    double IntrinsicWidth,
    double IntrinsicHeight,
    double AspectRatio,
    int OutputLength,
    int Stride,
    int Width,
    int Height,
    MarkdownSvgPixelFormat PixelFormat,
    bool HasText,
    bool UsesCurrentColor,
    bool UsesColorScheme,
    string Detail,
    long WorkerInstanceId = 0,
    long ReadyFontGeneration = 0);

internal static class WorkerProtocol
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const int RequestSize = 512;
    public const int ResponseSize = 256;
    public const uint Magic = 0x4756534d;
    public const ushort Version = 9;
    public const string WorkerFileName = "MarkdownRenderer.Svg.Resvg.Worker.exe";

    public static byte[] Encode(in WorkerRequest request)
    {
        byte[] frame = new byte[RequestSize];
        Encode(in request, frame);
        return frame;
    }

    public static void Encode(in WorkerRequest request, Span<byte> destination)
    {
        if (destination.Length != RequestSize)
            throw new ArgumentException($"A worker request frame must be exactly {RequestSize} bytes.", nameof(destination));

        // This encoder is also used with a reusable per-worker frame. Clear
        // reserved fields and any bytes left by a previous request before
        // validating or writing the current one.
        destination.Clear();
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.ContentHash);
        if (request.ContentHash.Length != 32)
            throw new ArgumentException("A SHA-256 content hash is required.", nameof(request));
        if (request.Locale.Length > 63 || Encoding.UTF8.GetByteCount(request.Locale) > 63)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent, "The SVG locale is too long.");
        bool needsMapping = request.Operation is WorkerOperation.Open or WorkerOperation.Render or WorkerOperation.FontCatalog;
        if (needsMapping != (request.MappingHandle != 0) || request.MappingHandle == ulong.MaxValue)
            throw new InvalidOperationException("The SVG request has an invalid shared-memory handle.");

        Span<byte> bytes = destination;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)request.Operation);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], request.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], request.NonceLow);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], request.NonceHigh);
        if (request.Operation == WorkerOperation.FontCatalog)
        {
            // This operation transfers only installed-font sections. No SVG
            // policy, image dimensions, locale or color fields apply; keep the
            // entire reserved frame zero rather than implying raster work.
            if (request.SourceLength <= InstalledFontCatalogTransfer.CatalogHeaderBytes ||
                (request.SourceLength - InstalledFontCatalogTransfer.CatalogHeaderBytes) %
                    InstalledFontCatalogTransfer.CatalogEntryBytes != 0 ||
                request.SourceLength > InstalledFontCatalogTransfer.CatalogHeaderBytes +
                    InstalledFontCatalogTransfer.MaximumFontFiles * InstalledFontCatalogTransfer.CatalogEntryBytes ||
                request.OutputLength != 0 || request.TargetWidth != 0 || request.TargetHeight != 0 ||
                request.Tile is not null || request.SemanticColor is not null || request.PhysicalViewport is not null || request.DocumentId != 0 ||
                request.Locale.Length != 0 || request.ContentHash.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                throw new WorkerProtocolException("The SVG font catalog request has invalid fields.");
            WorkerFontCatalogGeneration.ValidateRequest(request.FontGeneration);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], checked((uint)request.SourceLength));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[120..], checked((uint)request.FontGeneration));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[240..], request.MappingHandle);
            return;
        }
        if (request.PhysicalViewport is { } viewport &&
            (request.Operation != WorkerOperation.Render ||
                !PhysicalViewportValidation.IsValid(
                    viewport,
                    request.TargetWidth,
                    request.TargetHeight)))
        {
            throw new WorkerProtocolException("The SVG request has an invalid fractional physical viewport.");
        }

        uint flags = request.Tile is null ? 0u : 1u;
        if (request.SemanticColor is not null)
            flags |= 2u;
        if (request.PhysicalViewport is not null)
            flags |= 4u;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[32..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], checked((uint)request.SourceLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], checked((uint)request.OutputLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], checked((uint)request.TargetWidth));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[48..], checked((uint)request.TargetHeight));
        if (request.Tile is { } tile)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[52..], tile.X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], tile.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[60..], checked((uint)tile.Width));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], checked((uint)tile.Height));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[68..], checked((uint)request.Options.MaxXmlDepth));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[72..], checked((uint)request.Options.MaxNestedSvgDepth));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[76..], checked((uint)request.Options.MaxStructuralCost));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[80..], checked((ulong)request.Options.MaxEmbeddedImageBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[88..], checked((ulong)request.Options.MaxEmbeddedImagePixels));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[96..], checked((ulong)request.Options.MaxOutputRasterBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[104..], checked((ulong)request.Options.MaxFilterIntermediateBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[112..], checked((ulong)request.ParsedResourceCacheBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[120..], checked((uint)request.FontGeneration));
        bytes[124] = request.ColorScheme == MarkdownSvgColorScheme.Dark ? (byte)2 : (byte)1;
        bytes[125] = request.PixelFormat == MarkdownSvgPixelFormat.Bgra8Premultiplied ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[128..], PackColor(request.SemanticColor));
        request.ContentHash.CopyTo(bytes[136..168]);
        int localeLength = Encoding.UTF8.GetBytes(request.Locale, bytes[176..240]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[168..], checked((ushort)localeLength));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[240..], request.MappingHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[368..], request.DocumentId);
        if (request.PhysicalViewport is { } physicalViewport)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes[376..], BitConverter.DoubleToInt64Bits(physicalViewport.Width));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[384..], BitConverter.DoubleToInt64Bits(physicalViewport.Height));
        }
    }

    public static WorkerResponse DecodeResponse(
        ReadOnlySpan<byte> bytes,
        ulong expectedRequestId,
        ulong expectedNonceLow,
        ulong expectedNonceHigh,
        WorkerOperation? expectedOperation = null)
    {
        if (bytes.Length != ResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) != expectedRequestId ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]) != expectedNonceLow ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]) != expectedNonceHigh)
        {
            throw new WorkerProtocolException("The worker returned a mismatched protocol frame.");
        }
        ushort rawStatus = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (!Enum.IsDefined((WorkerStatus)rawStatus))
            throw new WorkerProtocolException("The worker returned an unknown status.");
        int detailLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[74..]);
        if (detailLength > 127)
            throw new WorkerProtocolException("The worker returned an invalid detail length.");
        if (bytes[(80 + detailLength)..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new WorkerProtocolException("The worker returned nonzero detail padding.");
        }
        string detail;
        try
        {
            detail = s_strictUtf8.GetString(bytes.Slice(80, detailLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkerProtocolException("The worker returned invalid UTF-8.", exception);
        }

        byte rawFormat = bytes[72];
        MarkdownSvgPixelFormat format = rawFormat switch
        {
            1 => MarkdownSvgPixelFormat.Rgba8Premultiplied,
            2 => MarkdownSvgPixelFormat.Bgra8Premultiplied,
            0 when expectedOperation == WorkerOperation.FontCatalog &&
                bytes[32..74].IndexOfAnyExcept((byte)0) < 0 => MarkdownSvgPixelFormat.Rgba8Premultiplied,
            0 when rawStatus != 0 => MarkdownSvgPixelFormat.Rgba8Premultiplied,
            _ => throw new WorkerProtocolException("The worker returned an invalid pixel format."),
        };
        byte flags = bytes[73];
        if ((flags & ~7) != 0)
            throw new WorkerProtocolException("The worker returned unknown metadata flags.");

        return new WorkerResponse(
            (WorkerStatus)rawStatus,
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[32..])),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[40..])),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[48..])),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..])),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..])),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[64..])),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..])),
            format,
            (flags & 1) != 0,
            (flags & 2) != 0,
            (flags & 4) != 0,
            detail,
            ReadyFontGeneration: BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..]));
    }

    private static uint PackColor(MarkdownSvgColor? color) => color is { } value
        ? (uint)(value.Red | value.Green << 8 | value.Blue << 16 | value.Alpha << 24)
        : uint.MaxValue;
}

internal sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
