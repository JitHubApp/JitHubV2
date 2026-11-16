using System;
using System.Buffers.Binary;

namespace MarkdownRenderer.Images.Internal.Raster;

/// <summary>
/// Bounded GIF header reader for <see cref="RasterImageHeaderReader"/>. It
/// walks every block up to the trailer and validates each image descriptor
/// without decoding pixels.
/// </summary>
internal static class RasterGifHeaderReader
{
    internal static RasterImageHeader? TryRead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 13 ||
            (!bytes[..6].SequenceEqual("GIF87a"u8) && !bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        long canvasPixels = (long)width * height;
        if (width <= 0 || height <= 0 || width > RasterImageResourceBudget.MaxDimension || height > RasterImageResourceBudget.MaxDimension ||
            canvasPixels > RasterImageResourceBudget.MaxDownsampleSourcePixels)
        {
            return null;
        }

        int offset = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            int tableBytes = 3 * (1 << ((bytes[10] & 0x07) + 1));
            if (!TryAdvance(ref offset, tableBytes, bytes.Length))
                return null;
        }

        int frames = 0;
        long framePixels = 0;
        (int Left, int Top, int Width, int Height)? firstFrame = null;
        while (offset < bytes.Length)
        {
            byte marker = bytes[offset++];
            if (marker == 0x3B)
            {
                // The logical canvas is the display/intrinsic geometry; WIC
                // exposes just the first descriptor's frame axes. Keep both.
                return frames == 0 ? null : new RasterImageHeader(
                    "GIF", width, height, frames, Math.Max(canvasPixels, framePixels), firstFrame);
            }

            if (marker == 0x21)
            {
                if (!TryAdvance(ref offset, 1, bytes.Length) || !TrySkipSubBlocks(bytes, ref offset))
                    return null;
                continue;
            }

            if (marker != 0x2C ||
                !TryReadImage(bytes, ref offset, width, height, ref firstFrame, ref framePixels))
            {
                return null;
            }
            frames++;
        }

        // A complete admitted source must include the GIF trailer, even if
        // an earlier frame was valid. Do not accept truncated later content.
        return null;
    }

    // Reads one image descriptor at offset (just past its 0x2C marker), its
    // local color table and its image data sub-blocks, and adds the frame's
    // drawn pixels to framePixels.
    private static bool TryReadImage(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int width,
        int height,
        ref (int Left, int Top, int Width, int Height)? firstFrame,
        ref long framePixels)
    {
        if (offset > bytes.Length - 9)
            return false;

        int left = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        int top = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]);
        int frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]);
        int frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]);
        // Validate every descriptor, not only the one we will draw.
        // Subtraction avoids overflow in right/bottom comparisons.
        if (frameWidth <= 0 || frameHeight <= 0)
            return false;
        bool insideCanvas = left <= width && top <= height &&
            frameWidth <= width - left && frameHeight <= height - top;
        if (firstFrame is null)
        {
            // The first frame is the one drawn; it must lie on the canvas.
            if (!insideCanvas)
                return false;
            firstFrame = (left, top, frameWidth, frameHeight);
            framePixels = checked(framePixels + (long)frameWidth * frameHeight);
        }
        else
        {
            // Browsers clip a later frame to the logical canvas
            // (a 640x386 frame on a 640x384 canvas). Count only the
            // visible area instead of rejecting the whole image.
            long visibleWidth = Math.Max(0, Math.Min(frameWidth, (long)width - left));
            long visibleHeight = Math.Max(0, Math.Min(frameHeight, (long)height - top));
            framePixels = checked(framePixels + visibleWidth * visibleHeight);
        }
        byte packed = bytes[offset + 8];
        offset += 9;
        if ((packed & 0x80) != 0)
        {
            int tableBytes = 3 * (1 << ((packed & 0x07) + 1));
            if (!TryAdvance(ref offset, tableBytes, bytes.Length))
                return false;
        }

        return TryAdvance(ref offset, 1, bytes.Length) && TrySkipSubBlocks(bytes, ref offset);
    }

    private static bool TrySkipSubBlocks(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            int length = bytes[offset++];
            if (length == 0)
            {
                return true;
            }

            if (!TryAdvance(ref offset, length, bytes.Length))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryAdvance(ref int offset, int amount, int length)
    {
        if (amount < 0 || offset > length - amount)
        {
            return false;
        }

        offset += amount;
        return true;
    }
}
