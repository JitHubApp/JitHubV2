namespace MarkdownRenderer.Images;

/// <summary>Identifies a safe, actionable SVG-provider failure category.</summary>
public enum MarkdownSvgFailureReason
{
    /// <summary>The document contains syntax outside the supported static subset.</summary>
    UnsupportedContent,

    /// <summary>A source, structure, decoded-resource, raster, or intermediate limit was exceeded.</summary>
    ResourceLimitExceeded,

    /// <summary>The hard operation deadline elapsed.</summary>
    Timeout,

    /// <summary>The isolated worker failed, crashed, or returned an invalid response.</summary>
    WorkerFailure,

    /// <summary>No compatible worker exists for the current process architecture.</summary>
    ArchitectureMismatch,

    /// <summary>The request was canceled before a safe result could be returned.</summary>
    Canceled,
}
