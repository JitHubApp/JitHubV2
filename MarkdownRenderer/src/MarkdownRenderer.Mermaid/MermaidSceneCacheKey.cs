using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.Mermaid;

internal readonly record struct MermaidSceneCacheKey(
    string SourceSha256,
    int SourceByteLength,
    string MermaidCompatibilityVersion,
    string MermanCommit,
    uint NativeAbiVersion,
    MermaidSceneVersion MmirVersion,
    MermaidLayoutMode Layout,
    MermaidThemeVariant Theme,
    int MaxSourceBytes,
    int MaxNodes,
    int MaxEdges,
    int MaxDepth,
    int MaxLabelBytes,
    int MaxSceneBytes,
    long MaxWorkingMemoryBytes,
    long DeadlineTicks,
    int MaxConcurrentRenders,
    int MaxOutstandingRenders,
    long MaxOutstandingSourceBytes,
    string FontCatalogFingerprint,
    string DiagnosticCultureName)
{
    private const int Utf8HashBufferBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal const string PinnedMermaidCompatibilityVersion = "11.16.1";
    internal const string PinnedMermanCommit = "ac53f21ba97e5bd8da7f849fbcdffe64695171e6";

    internal static MermaidSceneCacheKey Create(
        ReadOnlySpan<byte> sourceUtf8,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
        => CreateCore(
            Convert.ToHexStringLower(SHA256.HashData(sourceUtf8)),
            sourceUtf8.Length,
            options,
            fontCatalog,
            diagnosticCulture);

    /// <summary>
    /// Hashes UTF-8 incrementally so cache lookup and same-key joining do not
    /// require every waiter to retain a full encoded copy before admission.
    /// </summary>
    internal static MermaidSceneCacheKey Create(
        string source,
        int sourceByteLength,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceByteLength < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceByteLength));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontCatalog);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Utf8HashBufferBytes);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Encoder encoder = StrictUtf8.GetEncoder();
            ReadOnlySpan<char> remaining = source.AsSpan();
            bool completed;
            do
            {
                encoder.Convert(
                    remaining,
                    buffer.AsSpan(0, Utf8HashBufferBytes),
                    flush: true,
                    out int charsUsed,
                    out int bytesUsed,
                    out completed);
                if (bytesUsed != 0)
                    hash.AppendData(buffer.AsSpan(0, bytesUsed));
                remaining = remaining[charsUsed..];
            }
            while (!completed);

            return CreateCore(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                sourceByteLength,
                options,
                fontCatalog,
                diagnosticCulture);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static MermaidSceneCacheKey CreateCore(
        string sourceSha256,
        int sourceByteLength,
        MermaidRenderOptions options,
        MermaidFontCatalog fontCatalog,
        CultureInfo diagnosticCulture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontCatalog);
        MermaidRenderBudgets budgets = options.Budgets;
        return new MermaidSceneCacheKey(
            sourceSha256,
            sourceByteLength,
            PinnedMermaidCompatibilityVersion,
            PinnedMermanCommit,
            NativeMethods.PackedAbiVersion,
            MermaidSceneVersion.Current,
            options.Layout,
            options.Theme,
            budgets.MaxSourceBytes,
            budgets.MaxNodes,
            budgets.MaxEdges,
            budgets.MaxDepth,
            budgets.MaxLabelBytes,
            budgets.MaxSceneBytes,
            budgets.MaxWorkingMemoryBytes,
            budgets.Deadline.Ticks,
            budgets.MaxConcurrentRenders,
            budgets.MaxOutstandingRenders,
            budgets.MaxOutstandingSourceBytes,
            fontCatalog.Fingerprint,
            diagnosticCulture.Name);
    }
}
