using System;

namespace MarkdownRenderer.Images.Raster;

/// <summary>A typed static raster decoding failure.</summary>
public sealed class MarkdownRasterException : Exception
{
    /// <summary>Initializes a typed decoder failure without embedding image data.</summary>
    public MarkdownRasterException(MarkdownRasterFailureReason reason, string? message = null,
        Exception? innerException = null)
        : base(message ?? "The raster image could not be decoded.", innerException)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
    }

    /// <summary>Gets the stable reason for the failure.</summary>
    public MarkdownRasterFailureReason Reason { get; }
}
