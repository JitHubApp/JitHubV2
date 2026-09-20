namespace MarkdownRenderer.Svg.Resvg;

/// <summary>Immutable, host-lowerable policy for the isolated resvg provider.</summary>
public sealed class ResvgMarkdownSvgRendererOptions
{
    public const int HardMaxSourceBytes = 8 * 1024 * 1024;
    public const int HardMaxXmlDepth = 128;
    public const int HardMaxNestedSvgDepth = 4;
    public const int HardMaxStructuralCost = 100_000;
    public const long HardMaxEmbeddedImageBytes = 64L * 1024 * 1024;
    public const long HardMaxEmbeddedImagePixels = HardMaxEmbeddedImageBytes / 4;
    public const long HardMaxOutputRasterBytes = 64L * 1024 * 1024;
    public const long HardMaxFilterIntermediateBytes = 128L * 1024 * 1024;
    public static readonly TimeSpan HardMaxRequestDeadline = TimeSpan.FromSeconds(3);

    public ResvgMarkdownSvgRendererOptions(
        int maxSourceBytes = HardMaxSourceBytes,
        int maxXmlDepth = HardMaxXmlDepth,
        int maxNestedSvgDepth = HardMaxNestedSvgDepth,
        int maxStructuralCost = HardMaxStructuralCost,
        long maxEmbeddedImageBytes = HardMaxEmbeddedImageBytes,
        long maxEmbeddedImagePixels = HardMaxEmbeddedImagePixels,
        long maxOutputRasterBytes = HardMaxOutputRasterBytes,
        long maxFilterIntermediateBytes = HardMaxFilterIntermediateBytes,
        TimeSpan? requestDeadline = null,
        int queueCapacity = 32,
        long? parsedResourceCacheBytes = null,
        long? workerCommitBytes = null,
        string? workerExecutablePath = null)
    {
        MaxSourceBytes = InRange(maxSourceBytes, HardMaxSourceBytes, nameof(maxSourceBytes));
        MaxXmlDepth = InRange(maxXmlDepth, HardMaxXmlDepth, nameof(maxXmlDepth));
        MaxNestedSvgDepth = InRange(maxNestedSvgDepth, HardMaxNestedSvgDepth, nameof(maxNestedSvgDepth));
        MaxStructuralCost = InRange(maxStructuralCost, HardMaxStructuralCost, nameof(maxStructuralCost));
        MaxEmbeddedImageBytes = InRange(maxEmbeddedImageBytes, HardMaxEmbeddedImageBytes, nameof(maxEmbeddedImageBytes));
        MaxEmbeddedImagePixels = InRange(maxEmbeddedImagePixels, HardMaxEmbeddedImagePixels, nameof(maxEmbeddedImagePixels));
        MaxOutputRasterBytes = InRange(maxOutputRasterBytes, HardMaxOutputRasterBytes, nameof(maxOutputRasterBytes));
        MaxFilterIntermediateBytes = InRange(maxFilterIntermediateBytes, HardMaxFilterIntermediateBytes, nameof(maxFilterIntermediateBytes));
        RequestDeadline = requestDeadline ?? HardMaxRequestDeadline;
        if (RequestDeadline <= TimeSpan.Zero || RequestDeadline > HardMaxRequestDeadline)
            throw new ArgumentOutOfRangeException(nameof(requestDeadline));
        QueueCapacity = InRange(queueCapacity, 32, nameof(queueCapacity));

        long architectureCacheMaximum = Environment.Is64BitProcess ? 64L * 1024 * 1024 : 32L * 1024 * 1024;
        ParsedResourceCacheBytes = InRange(parsedResourceCacheBytes ?? architectureCacheMaximum, architectureCacheMaximum, nameof(parsedResourceCacheBytes));
        long architectureCommitMaximum = Environment.Is64BitProcess ? 384L * 1024 * 1024 : 192L * 1024 * 1024;
        WorkerCommitBytes = InRange(workerCommitBytes ?? architectureCommitMaximum, architectureCommitMaximum, nameof(workerCommitBytes));
        WorkerExecutablePath = string.IsNullOrWhiteSpace(workerExecutablePath)
            ? null
            : Path.GetFullPath(workerExecutablePath);
    }

    public int MaxSourceBytes { get; }
    public int MaxXmlDepth { get; }
    public int MaxNestedSvgDepth { get; }
    public int MaxStructuralCost { get; }
    public long MaxEmbeddedImageBytes { get; }
    public long MaxEmbeddedImagePixels { get; }
    public long MaxOutputRasterBytes { get; }
    public long MaxFilterIntermediateBytes { get; }
    public TimeSpan RequestDeadline { get; }
    public int QueueCapacity { get; }
    public long ParsedResourceCacheBytes { get; }
    public long WorkerCommitBytes { get; }
    public string? WorkerExecutablePath { get; }

    private static int InRange(int value, int maximum, string name)
    {
        if (value <= 0 || value > maximum)
            throw new ArgumentOutOfRangeException(name);
        return value;
    }

    private static long InRange(long value, long maximum, string name)
    {
        if (value <= 0 || value > maximum)
            throw new ArgumentOutOfRangeException(name);
        return value;
    }
}
