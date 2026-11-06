namespace JitHub.Markdown.Images;

/// <summary>Size limits shared by the GitHub image cache and Markdown image resolution.</summary>
public static class GitHubImageLimits
{
    /// <summary>The largest image payload, in bytes, that is fetched, decoded or cached.</summary>
    /// <remarks>
    /// Large project READMEs commonly contain optimized screenshots and animated
    /// demonstrations above 10 MiB. Compressed bytes remain bounded here while
    /// RasterImageResourceBudget independently caps dimensions, frames, pixels,
    /// and decoded memory before the renderer admits the payload.
    /// </remarks>
    public const int MaxImageBytes = 32 * 1024 * 1024;
}
