using System;
using System.Collections.Generic;
using System.IO;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Determines how a file should be rendered in the code viewer.
/// </summary>
public sealed class FilePreviewResolver : IFilePreviewResolver
{
    public const long MaximumInteractiveTextBytes = 128 * 1024;
    public const long MaximumSvgBytes = RepositorySvgSecurityPolicy.MaxInputBytes;
    private const long MaximumImageBytes = 10 * 1024 * 1024;
    private const long MaxHexBytes = 256 * 1024;          // 256 KB
    private const int BinarySniffLength = 8 * 1024;       // 8 KB
    private const double NonPrintableThreshold = 0.30;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico",
        ".tif", ".tiff", ".heic", ".heif", ".webp",
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx",
    };

    private readonly ILanguageIdResolver _languageResolver;

    public FilePreviewResolver(ILanguageIdResolver languageResolver)
    {
        _languageResolver = languageResolver;
    }

    public FilePreviewDescriptor Resolve(string path, long byteSize, ReadOnlyMemory<byte> headSample)
    {
        string ext = Path.GetExtension(path);

        // Images do not enter the editor and have their own bounded decoder path.
        if (ImageExtensions.Contains(ext))
        {
            if (byteSize > MaximumImageBytes)
                return new FilePreviewDescriptor(RepoFilePreviewKind.TooLarge, "text", null, false);

            string mime = GetImageMime(ext);
            return new FilePreviewDescriptor(RepoFilePreviewKind.Image, "text", mime, true);
        }

        // Binary files use a separate bounded hex path and must be classified
        // before applying the synchronous text-editor budget.
        if (IsBinary(headSample))
        {
            return byteSize <= MaxHexBytes
                ? new FilePreviewDescriptor(RepoFilePreviewKind.Hex, "text", null, true)
                : new FilePreviewDescriptor(RepoFilePreviewKind.Unsupported, "text", null, true);
        }

        // SVG has a dedicated bounded parser/render path. Classify it before the
        // synchronous editor budget so safe repository diagrams can exceed the
        // text editor limit without ever entering Scintilla.
        if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return byteSize <= MaximumSvgBytes
                ? new FilePreviewDescriptor(RepoFilePreviewKind.Svg, "xml", null, false)
                : new FilePreviewDescriptor(RepoFilePreviewKind.TooLarge, "text", null, false);
        }

        // Scintilla SetText is synchronous. Route large text to the lightweight
        // native fallback before creating an editor so navigation never monopolizes
        // the UI thread. The fallback keeps open/copy-link access to the full file.
        if (byteSize > MaximumInteractiveTextBytes)
            return new FilePreviewDescriptor(RepoFilePreviewKind.TooLarge, "text", null, false);

        // Markdown.
        if (MarkdownExtensions.Contains(ext))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Markdown, "markdown", null, false);

        // CSV / TSV.
        if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Csv, "text", null, false);
        if (string.Equals(ext, ".tsv", StringComparison.OrdinalIgnoreCase))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Csv, "text", null, false);

        // JSON.
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".json5", StringComparison.OrdinalIgnoreCase))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Json, "json", null, false);

        // XML-family and HTML.
        if (string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xsd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xslt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Xml, "xml", null, false);

        // YAML.
        if (string.Equals(ext, ".yml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".yaml", StringComparison.OrdinalIgnoreCase))
            return new FilePreviewDescriptor(RepoFilePreviewKind.Yaml, "yaml", null, false);

        // Code - resolve language id.
        string languageId = _languageResolver.Resolve(path, headSample.IsEmpty ? default : headSample.Span);
        return new FilePreviewDescriptor(RepoFilePreviewKind.Code, languageId, null, false);
    }

    private static bool IsBinary(ReadOnlyMemory<byte> sample)
    {
        if (sample.IsEmpty) return false;

        ReadOnlySpan<byte> span = sample.Length > BinarySniffLength
            ? sample.Span.Slice(0, BinarySniffLength)
            : sample.Span;

        int nonPrintable = 0;
        foreach (byte b in span)
        {
            if (b == 0) return true; // null byte → definitely binary
            if (b < 9 || (b > 13 && b < 32) || b == 127)
                nonPrintable++;
        }

        return (double)nonPrintable / span.Length > NonPrintableThreshold;
    }

    private static string GetImageMime(string ext) => ext.ToLowerInvariant() switch
    {
        ".png"  => "image/png",
        ".jpg"  => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".gif"  => "image/gif",
        ".bmp"  => "image/bmp",
        ".ico"  => "image/x-icon",
        ".tif"  => "image/tiff",
        ".tiff" => "image/tiff",
        ".heic" => "image/heif",
        ".heif" => "image/heif",
        ".webp" => "image/webp",
        _       => "application/octet-stream",
    };
}
