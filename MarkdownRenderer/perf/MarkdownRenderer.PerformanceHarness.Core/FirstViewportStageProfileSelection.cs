namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Source-size selection for the opt-in first-viewport diagnostic sidecar.
/// The values are intentionally limited to the three established source-size
/// conditions so this diagnostic cannot invent a new measurement population.
/// </summary>
public readonly record struct FirstViewportStageProfileSelection(int SourceUtf16Bytes)
{
    public const int DefaultSourceUtf16Bytes = 1024 * 1024;
    // The selector accepts these exact decimal UTF-16 byte counts only.
    public const string SourceBytesEnvironmentVariable =
        "MARKDOWNRENDERER_PERF_STAGE_PROFILE_SOURCE_BYTES";

    public static FirstViewportStageProfileSelection Resolve(
        bool quick,
        bool outputPathConfigured,
        string? configuredSourceBytes)
    {
        bool sourceSizeConfigured = !string.IsNullOrEmpty(configuredSourceBytes);
        if (sourceSizeConfigured && !outputPathConfigured)
        {
            throw new ArgumentException(
                $"{SourceBytesEnvironmentVariable} requires " +
                "MARKDOWNRENDERER_PERF_STAGE_PROFILE_PATH.");
        }

        if ((outputPathConfigured || sourceSizeConfigured) && !quick)
        {
            throw new ArgumentException(
                "First-viewport stage profiling is diagnostic-only and requires --quick.");
        }

        if (!outputPathConfigured)
            return new FirstViewportStageProfileSelection(DefaultSourceUtf16Bytes);

        int sourceUtf16Bytes = configuredSourceBytes switch
        {
            null or "" => DefaultSourceUtf16Bytes,
            "102400" => 100 * 1024,
            "1048576" => 1024 * 1024,
            "10485760" => 10 * 1024 * 1024,
            _ => throw new ArgumentException(
                $"{SourceBytesEnvironmentVariable} must be exactly 102400, 1048576, or 10485760 bytes."),
        };

        return new FirstViewportStageProfileSelection(sourceUtf16Bytes);
    }
}
