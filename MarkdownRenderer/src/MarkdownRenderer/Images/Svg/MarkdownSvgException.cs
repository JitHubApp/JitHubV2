using System;

namespace MarkdownRenderer.Images.Svg;

/// <summary>An SVG-provider failure with a stable host-facing reason.</summary>
public sealed class MarkdownSvgException : Exception
{
    /// <summary>Initializes a typed SVG-provider failure.</summary>
    public MarkdownSvgException(
        MarkdownSvgFailureReason reason,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? GetDefaultMessage(reason), innerException)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        Reason = reason;
    }

    /// <summary>Gets the stable failure reason suitable for fallback selection.</summary>
    public MarkdownSvgFailureReason Reason { get; }

    private static string GetDefaultMessage(MarkdownSvgFailureReason reason) => reason switch
    {
        MarkdownSvgFailureReason.UnsupportedContent => "The SVG contains unsupported content.",
        MarkdownSvgFailureReason.ResourceLimitExceeded => "The SVG exceeds a resource limit.",
        MarkdownSvgFailureReason.Timeout => "The SVG operation timed out.",
        MarkdownSvgFailureReason.WorkerFailure => "The SVG worker failed.",
        MarkdownSvgFailureReason.ArchitectureMismatch => "The SVG worker does not support this architecture.",
        MarkdownSvgFailureReason.Canceled => "The SVG operation was canceled.",
        _ => "The SVG operation failed.",
    };
}
