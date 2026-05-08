using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JitHub.Models.CodeViewer;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public class FilePreviewResolverTests
{
    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class StubLanguageIdResolver : ILanguageIdResolver
    {
        private readonly Dictionary<string, string> _map;
        private readonly string _default;

        public StubLanguageIdResolver(Dictionary<string, string>? map = null, string defaultLang = "plaintext")
        {
            _map = map ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _default = defaultLang;
        }

        public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default)
        {
            string ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && _map.TryGetValue(ext, out var lang))
                return lang;
            return _default;
        }

        public bool IsKnown(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return !string.IsNullOrEmpty(ext) && _map.ContainsKey(ext);
        }
    }

    private static FilePreviewResolver CreateResolver(Dictionary<string, string>? map = null)
        => new FilePreviewResolver(new StubLanguageIdResolver(map));

    private static ReadOnlyMemory<byte> TextBytes(string s) =>
        new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(s));

    private static ReadOnlyMemory<byte> BinaryBytes() =>
        new ReadOnlyMemory<byte>(new byte[] { 0x00, 0x01, 0x02, 0x03 });

    private static ReadOnlyMemory<byte> HighNonPrintableBytes()
    {
        // 40% non-printable (control chars), above 30% threshold
        var bytes = new byte[100];
        for (int i = 0; i < 40; i++) bytes[i] = 0x01; // non-printable
        for (int i = 40; i < 100; i++) bytes[i] = (byte)'a';
        return new ReadOnlyMemory<byte>(bytes);
    }

    // ── Size guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FileSizeExceedsMaximum_ReturnsTooLarge()
    {
        var resolver = CreateResolver();
        long overMax = 5L * 1024 * 1024 + 1;
        var result = resolver.Resolve("file.txt", overMax, default);
        Assert.Equal(RepoFilePreviewKind.TooLarge, result.Kind);
    }

    [Fact]
    public void Resolve_FileSizeExactlyAtMax_NotTooLarge()
    {
        var resolver = CreateResolver();
        long atMax = 5L * 1024 * 1024;
        var result = resolver.Resolve("file.txt", atMax, TextBytes("hello"));
        Assert.NotEqual(RepoFilePreviewKind.TooLarge, result.Kind);
    }

    [Fact]
    public void Resolve_FileSizeUnderMax_NotTooLarge()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.txt", 100, TextBytes("hello"));
        Assert.NotEqual(RepoFilePreviewKind.TooLarge, result.Kind);
    }

    // ── Image extensions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".bmp", "image/bmp")]
    [InlineData(".ico", "image/x-icon")]
    [InlineData(".tif", "image/tiff")]
    [InlineData(".tiff", "image/tiff")]
    [InlineData(".heic", "image/heif")]
    [InlineData(".heif", "image/heif")]
    [InlineData(".webp", "image/webp")]
    public void Resolve_ImageExtension_ReturnsImageKindWithCorrectMime(string ext, string expectedMime)
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve($"photo{ext}", 1024, default);
        Assert.Equal(RepoFilePreviewKind.Image, result.Kind);
        Assert.Equal(expectedMime, result.ImageMimeType);
    }

    [Fact]
    public void Resolve_ImageExtension_IsLikelyBinaryTrue()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("photo.png", 1024, default);
        Assert.True(result.IsLikelyBinary);
    }

    // ── SVG ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_SvgExtension_ReturnsSvgKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("icon.svg", 512, default);
        Assert.Equal(RepoFilePreviewKind.Svg, result.Kind);
        Assert.Equal("xml", result.LanguageId);
        Assert.False(result.IsLikelyBinary);
    }

    // ── Markdown ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".md")]
    [InlineData(".markdown")]
    [InlineData(".mdx")]
    public void Resolve_MarkdownExtension_ReturnsMarkdownKind(string ext)
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve($"README{ext}", 100, default);
        Assert.Equal(RepoFilePreviewKind.Markdown, result.Kind);
        Assert.Equal("markdown", result.LanguageId);
    }

    // ── CSV / TSV ─────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_CsvExtension_ReturnsCsvKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("data.csv", 100, default);
        Assert.Equal(RepoFilePreviewKind.Csv, result.Kind);
    }

    [Fact]
    public void Resolve_TsvExtension_ReturnsCsvKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("data.tsv", 100, default);
        Assert.Equal(RepoFilePreviewKind.Csv, result.Kind);
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_JsonExtension_ReturnsJsonKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("config.json", 100, default);
        Assert.Equal(RepoFilePreviewKind.Json, result.Kind);
        Assert.Equal("json", result.LanguageId);
    }

    [Fact]
    public void Resolve_Json5Extension_ReturnsJsonKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("config.json5", 100, default);
        Assert.Equal(RepoFilePreviewKind.Json, result.Kind);
    }

    // ── XML family ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".xml")]
    [InlineData(".xsd")]
    [InlineData(".xslt")]
    [InlineData(".html")]
    [InlineData(".htm")]
    public void Resolve_XmlFamilyExtension_ReturnsXmlKind(string ext)
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve($"file{ext}", 100, default);
        Assert.Equal(RepoFilePreviewKind.Xml, result.Kind);
        Assert.Equal("xml", result.LanguageId);
    }

    // ── YAML ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".yml")]
    [InlineData(".yaml")]
    public void Resolve_YamlExtension_ReturnsYamlKind(string ext)
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve($"config{ext}", 100, default);
        Assert.Equal(RepoFilePreviewKind.Yaml, result.Kind);
        Assert.Equal("yaml", result.LanguageId);
    }

    // ── Binary detection ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NullByteInSample_DetectedAsBinary()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.bin", 100, BinaryBytes());
        Assert.True(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_HighNonPrintableRatio_DetectedAsBinary()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.bin", 100, HighNonPrintableBytes());
        Assert.True(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_CleanTextSample_NotDetectedAsBinary()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.txt", 100, TextBytes("hello world, this is clean text"));
        Assert.False(result.IsLikelyBinary);
    }

    // ── Binary size routing ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_BinarySmallFile_ReturnsHex()
    {
        var resolver = CreateResolver();
        long smallSize = 100;
        var result = resolver.Resolve("file.bin", smallSize, BinaryBytes());
        Assert.Equal(RepoFilePreviewKind.Hex, result.Kind);
    }

    [Fact]
    public void Resolve_BinaryLargeFile_ReturnsUnsupported()
    {
        var resolver = CreateResolver();
        long largeSize = 256L * 1024 + 1; // just over 256KB
        var result = resolver.Resolve("file.bin", largeSize, BinaryBytes());
        Assert.Equal(RepoFilePreviewKind.Unsupported, result.Kind);
    }

    [Fact]
    public void Resolve_BinaryExactlyAtHexBoundary_ReturnsHex()
    {
        var resolver = CreateResolver();
        long atBoundary = 256L * 1024; // exactly 256KB
        var result = resolver.Resolve("file.bin", atBoundary, BinaryBytes());
        Assert.Equal(RepoFilePreviewKind.Hex, result.Kind);
    }

    [Fact]
    public void Resolve_BinaryJustOverHexBoundary_ReturnsUnsupported()
    {
        var resolver = CreateResolver();
        long justOver = 256L * 1024 + 1;
        var result = resolver.Resolve("file.bin", justOver, BinaryBytes());
        Assert.Equal(RepoFilePreviewKind.Unsupported, result.Kind);
    }

    // ── Code / language id ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownExtensionNonBinary_ReturnsCodeKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.xyz", 100, TextBytes("some code here"));
        Assert.Equal(RepoFilePreviewKind.Code, result.Kind);
    }

    [Fact]
    public void Resolve_CsExtension_ReturnsCodeWithCsharpLanguageId()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".cs"] = "csharp" };
        var resolver = CreateResolver(map);
        var result = resolver.Resolve("Program.cs", 100, TextBytes("using System;"));
        Assert.Equal(RepoFilePreviewKind.Code, result.Kind);
        Assert.Equal("csharp", result.LanguageId);
    }

    [Fact]
    public void Resolve_UnknownExtensionNonBinary_LanguageIdFromResolver()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.xyz", 100, TextBytes("content"));
        Assert.Equal("plaintext", result.LanguageId);
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UpperCasePngExtension_ReturnsImageKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("photo.PNG", 1024, default);
        Assert.Equal(RepoFilePreviewKind.Image, result.Kind);
    }

    [Fact]
    public void Resolve_MixedCaseJpegExtension_ReturnsImageKind()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("photo.JpEg", 1024, default);
        Assert.Equal(RepoFilePreviewKind.Image, result.Kind);
    }

    [Fact]
    public void Resolve_UpperCaseCsExtension_ReturnsCodeWithCsharpLanguageId()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".cs"] = "csharp" };
        var resolver = CreateResolver(map);
        var result = resolver.Resolve("Program.CS", 100, TextBytes("code"));
        Assert.Equal(RepoFilePreviewKind.Code, result.Kind);
        Assert.Equal("csharp", result.LanguageId);
    }

    // ── Descriptor properties ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_TooLarge_IsLikelyBinaryFalse()
    {
        var resolver = CreateResolver();
        long overMax = 5L * 1024 * 1024 + 1;
        var result = resolver.Resolve("file.txt", overMax, default);
        Assert.False(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_SvgFile_ImageMimeTypeNull()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("icon.svg", 100, default);
        Assert.Null(result.ImageMimeType);
    }

    [Fact]
    public void Resolve_MarkdownFile_IsLikelyBinaryFalse()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("README.md", 100, default);
        Assert.False(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_BinaryHex_IsLikelyBinaryTrue()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("data.bin", 100, BinaryBytes());
        Assert.True(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_EmptySample_NotBinary()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("file.txt", 0, default);
        Assert.Equal(RepoFilePreviewKind.Code, result.Kind);
        Assert.False(result.IsLikelyBinary);
    }

    [Fact]
    public void Resolve_JsonFile_ImageMimeTypeNull()
    {
        var resolver = CreateResolver();
        var result = resolver.Resolve("data.json", 100, default);
        Assert.Null(result.ImageMimeType);
    }
}
