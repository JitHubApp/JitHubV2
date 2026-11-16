using System;

namespace MarkdownRenderer.Images.Raster;

/// <summary>Immutable decode settings for one borrowed encoded source.</summary>
/// <param name="EncodedBytes">
/// Source bytes admitted by the host's image policy. Keep the backing memory unchanged and alive
/// until DecodeAsync completes, including failure or cancellation. Providers must independently
/// validate the container and may not retain this memory after the operation completes.
/// </param>
/// <param name="Format">Expected container, independently checked against the bytes.</param>
/// <param name="TargetWidthPixels">Exact output width after EXIF orientation, without upscaling.</param>
/// <param name="TargetHeightPixels">Exact output height after EXIF orientation, without upscaling.</param>
/// <param name="MaximumWorkingBytes">
/// Host-lowered bound for source mapping, output and decoder-owned working allocations. This is
/// not a process RSS guarantee; providers must also enforce their own immutable process limits.
/// </param>
/// <param name="PixelFormat">Premultiplied output byte order.</param>
/// <param name="Priority">Scheduling class before work enters the decoder.</param>
public sealed record MarkdownRasterDecodeRequest(
    ReadOnlyMemory<byte> EncodedBytes,
    MarkdownRasterFormat Format,
    int TargetWidthPixels,
    int TargetHeightPixels,
    long MaximumWorkingBytes,
    MarkdownRasterPixelFormat PixelFormat = MarkdownRasterPixelFormat.Bgra8Premultiplied,
    MarkdownRasterDecodePriority Priority = MarkdownRasterDecodePriority.Visible);
