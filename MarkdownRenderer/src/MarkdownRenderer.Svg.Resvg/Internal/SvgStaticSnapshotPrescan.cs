namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// A cancellable, allocation-free byte scan that tells
/// <see cref="SvgStaticSnapshot"/> whether a source could contain anything the
/// static first frame rewrites, so ordinary SVGs skip the DOM load. A match
/// only means the DOM pass must run; it is not a validation result.
/// </summary>
internal static class SvgStaticSnapshotPrescan
{
    internal static bool MayRequireStaticSnapshot(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> marker = "<animate"u8;
        ReadOnlySpan<byte> scriptMarker = "<script"u8;
        ReadOnlySpan<byte> setMarker = "<set"u8;
        ReadOnlySpan<byte> discardMarker = "<discard"u8;
        ReadOnlySpan<byte> foreignObjectMarker = "<foreignObject"u8;
        ReadOnlySpan<byte> fontFaceMarker = "@font-face"u8;
        ReadOnlySpan<byte> importMarker = "@import"u8;
        ReadOnlySpan<byte> keyframesMarker = "@keyframes"u8;
        ReadOnlySpan<byte> webkitKeyframesMarker = "@-webkit-keyframes"u8;
        ReadOnlySpan<byte> animationMarker = "animation"u8;
        ReadOnlySpan<byte> transitionMarker = "transition"u8;
        for (int index = 0; index < bytes.Length; index++)
        {
            if ((index & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            byte first = ToAsciiLower(bytes[index]);
            if ((first == (byte)'<' &&
                    (AsciiStartsWithIgnoreCase(bytes[index..], marker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], scriptMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], setMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], discardMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], foreignObjectMarker))) ||
                (first == (byte)'@' &&
                    (AsciiStartsWithIgnoreCase(bytes[index..], fontFaceMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], importMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], keyframesMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], webkitKeyframesMarker))) ||
                (first == (byte)'a' && AsciiStartsWithIgnoreCase(bytes[index..], animationMarker)) ||
                (first == (byte)'t' && AsciiStartsWithIgnoreCase(bytes[index..], transitionMarker)))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToAsciiLower(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + ((byte)'a' - (byte)'A'))
        : value;

    private static bool AsciiStartsWithIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
            return false;

        for (int index = 0; index < prefix.Length; index++)
        {
            byte actual = value[index];
            byte expected = prefix[index];
            if (actual is >= (byte)'A' and <= (byte)'Z')
                actual = (byte)(actual + ((byte)'a' - (byte)'A'));
            if (expected is >= (byte)'A' and <= (byte)'Z')
                expected = (byte)(expected + ((byte)'a' - (byte)'A'));
            if (actual != expected)
                return false;
        }

        return true;
    }
}
