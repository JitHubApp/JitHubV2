using System;
using MarkdownRenderer.Images.Internal.Svg;

namespace MarkdownRenderer.Images;

/// <summary>Inert SVG bytes or a typed host-preflight rejection.</summary>
public sealed class MarkdownSvgSourcePreparation
{
    private readonly MarkdownSvgSourceInfo? _sourceInfo;

    private MarkdownSvgSourcePreparation(
        byte[]? sanitizedBytes,
        MarkdownSvgFailureReason? failureReason,
        string? failureDescription,
        MarkdownSvgSourceInfo? sourceInfo = null)
    {
        SanitizedBytes = sanitizedBytes;
        FailureReason = failureReason;
        FailureDescription = failureDescription;
        _sourceInfo = sourceInfo;
    }

    /// <summary>Gets the admitted static source, or null for a rejection.</summary>
    public byte[]? SanitizedBytes { get; }

    /// <summary>Gets the typed reason for a rejection.</summary>
    public MarkdownSvgFailureReason? FailureReason { get; }

    /// <summary>Gets a safe, human-readable rejection description.</summary>
    public string? FailureDescription { get; }

    /// <summary>Creates an admitted static source.</summary>
    public static MarkdownSvgSourcePreparation Admit(byte[] sanitizedBytes)
    {
        ArgumentNullException.ThrowIfNull(sanitizedBytes);
        return new MarkdownSvgSourcePreparation(sanitizedBytes, null, null);
    }

    /// <summary>
    /// Creates an admitted static source together with the information the
    /// renderer read from it, so the view does not read the source again.
    /// </summary>
    /// <param name="sanitizedBytes">The admitted static source.</param>
    /// <param name="sourceInfo">
    /// Information read from exactly <paramref name="sanitizedBytes"/> with a
    /// <see cref="MarkdownSvgSourceInfoReader"/>.
    /// </param>
    public static MarkdownSvgSourcePreparation Admit(
        byte[] sanitizedBytes,
        MarkdownSvgSourceInfo sourceInfo)
    {
        ArgumentNullException.ThrowIfNull(sanitizedBytes);
        ArgumentNullException.ThrowIfNull(sourceInfo);
        return new MarkdownSvgSourcePreparation(sanitizedBytes, null, null, sourceInfo);
    }

    internal bool TryGetPreparedMetadata(
        byte[] expectedSource,
        out SvgSourceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (ReferenceEquals(SanitizedBytes, expectedSource) && _sourceInfo is { } sourceInfo)
        {
            metadata = sourceInfo.Metadata;
            return true;
        }

        metadata = default;
        return false;
    }

    /// <summary>Creates a deterministic rejection without throwing.</summary>
    public static MarkdownSvgSourcePreparation Reject(
        MarkdownSvgFailureReason reason,
        string description)
    {
        if (reason is not (MarkdownSvgFailureReason.UnsupportedContent or
            MarkdownSvgFailureReason.ResourceLimitExceeded))
            throw new ArgumentOutOfRangeException(nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new MarkdownSvgSourcePreparation(null, reason, description);
    }
}
