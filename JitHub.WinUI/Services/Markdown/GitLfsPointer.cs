using System;
using System.Text;

namespace JitHub.Services.Markdown;

/// <summary>
/// Recognizes the small text pointer returned by GitHub's Contents and raw
/// content endpoints for a Git LFS object. The repository raw route resolves
/// that pointer to the immutable media object users see on github.com.
/// </summary>
internal static class GitLfsPointer
{
    private const int MaximumPointerBytes = 4 * 1024;
    private const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    public static bool IsPointer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumPointerBytes)
            return false;

        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || !lines[0].Equals(VersionLine, StringComparison.Ordinal))
            return false;

        string oid = lines[1];
        const string oidPrefix = "oid sha256:";
        if (!oid.StartsWith(oidPrefix, StringComparison.Ordinal) ||
            oid.Length != oidPrefix.Length + 64)
        {
            return false;
        }

        for (int index = oidPrefix.Length; index < oid.Length; index++)
        {
            if (!char.IsAsciiHexDigit(oid[index]))
                return false;
        }

        const string sizePrefix = "size ";
        return lines[2].StartsWith(sizePrefix, StringComparison.Ordinal) &&
            long.TryParse(
                lines[2].AsSpan(sizePrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long size) &&
            size >= 0;
    }
}
