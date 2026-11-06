namespace MarkdownRenderer.Images;

/// <summary>Stable categories for static raster decoder failures.</summary>
public enum MarkdownRasterFailureReason
{
    /// <summary>Malformed input or a container feature outside the supported static subset.</summary>
    UnsupportedContent,
    /// <summary>The source, decoded image, output or working memory exceeds a limit.</summary>
    ResourceLimitExceeded,
    /// <summary>The active-operation deadline elapsed.</summary>
    Timeout,
    /// <summary>The isolated decoder failed or returned an invalid response.</summary>
    WorkerFailure,
    /// <summary>No decoder worker is available for this architecture.</summary>
    ArchitectureMismatch,
    /// <summary>The operation was canceled or the provider was disposed.</summary>
    Canceled,
}
