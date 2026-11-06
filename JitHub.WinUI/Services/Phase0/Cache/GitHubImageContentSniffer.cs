using System;
using System.Buffers.Binary;
using System.IO;
using System.Xml;

namespace JitHub.Services;

/// <summary>
/// Identifies admitted image payloads by their byte signature rather than the
/// untrusted response label, and picks the cache file extension.
/// </summary>
internal static class GitHubImageContentSniffer
{
    internal static bool TryDetectSupportedImageContentType(
        byte[] bytes,
        string? declaredContentType,
        out string contentType)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            contentType = "image/png";
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            contentType = "image/jpeg";
            return true;
        }

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
            bytes[5] == (byte)'a')
        {
            contentType = "image/gif";
            return true;
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            contentType = "image/webp";
            return true;
        }

        if (IsStillAvif(bytes))
        {
            contentType = "image/avif";
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            contentType = "image/bmp";
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
        {
            contentType = "image/x-icon";
            return true;
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 0x2A && bytes[3] == 0) ||
             (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0 && bytes[3] == 0x2A)))
        {
            contentType = "image/tiff";
            return true;
        }

        // XML is not self-identifying enough to admit under an arbitrary image
        // label. SVG retains the stricter declared-type check before its existing
        // sandboxed parser and resource policy run.
        if (string.Equals(declaredContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "image/svg+xml";
                return true;
            }
        }

        contentType = string.Empty;
        return false;
    }

    internal static bool IsWellFormedSvg(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            // Match the SVG preflight's inert legacy external DOCTYPE support.
            // This check only detects incomplete downloads; the renderer's
            // preflight remains authoritative for forbidden DTD content.
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersInDocument = GitHubImageService.MaxImageBytes,
            MaxCharactersFromEntities = 1,
        };
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(input, settings);
            if (reader.MoveToContent() != XmlNodeType.Element ||
                !reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsStillAvif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (boxSize < 16 || boxSize > bytes.Length || (boxSize & 3) != 0)
        {
            return false;
        }

        bool hasStillBrand = false;
        for (int offset = 8; offset <= boxSize - 4; offset += 4)
        {
            ReadOnlySpan<byte> brand = bytes.Slice(offset, 4);
            if (brand.SequenceEqual("avis"u8))
            {
                // Animated AVIF has a different resource and decode budget. Do
                // not admit it through the bounded still-image path.
                return false;
            }

            hasStillBrand |= brand.SequenceEqual("avif"u8);
        }

        return hasStillBrand;
    }

    internal static string GetExtension(string mediaType, string sourceUrl)
    {
        string? extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "image/tiff" => ".tiff",
            _ => null
        };

        if (extension is not null)
        {
            return extension;
        }

        string pathExtension = Path.GetExtension(new Uri(sourceUrl).AbsolutePath);
        return string.IsNullOrWhiteSpace(pathExtension) || pathExtension.Length > 12
            ? ".img"
            : pathExtension;
    }
}
