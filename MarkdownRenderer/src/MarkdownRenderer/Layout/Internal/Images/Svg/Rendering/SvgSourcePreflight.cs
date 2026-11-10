using System;
using System.Security.Cryptography;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Svg;
using MarkdownRenderer.Internal.Hashing;
using Windows.Foundation;
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Reads what an image box needs from an SVG source before it opens a
/// document: the intrinsic size, the trimmed title and description, the
/// content hash and the security-partition hash.
/// </summary>
internal static class SvgSourcePreflight
{
    /// <summary>
    /// Hashes the source and partition, then reads intrinsic size and metadata
    /// unless the provider already rejected the source.
    /// </summary>
    internal static SvgPreflight Preflight(
        byte[] rawBytes,
        MarkdownSvgSourcePreparation preparation,
        Size intrinsicHint,
        string securityPartition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string contentHash = Convert.ToHexString(SHA256.HashData(rawBytes));
        string partitionHash = HashSecurityPartition(securityPartition);

        cancellationToken.ThrowIfCancellationRequested();
        if (preparation.FailureReason is not null)
            return (
                intrinsicHint,
                null,
                null,
                contentHash,
                partitionHash);

        SvgSourceMetadata metadata = preparation.TryGetPreparedMetadata(rawBytes, out SvgSourceMetadata preparedMetadata)
            ? preparedMetadata
            : SvgSourceMetadata.Extract(rawBytes, cancellationToken);
        Size intrinsic = intrinsicHint;
        if (intrinsic.Width <= 0 || intrinsic.Height <= 0)
        {
            double width = metadata.Geometry.IntrinsicWidth;
            double height = metadata.Geometry.IntrinsicHeight;
            if (width > 0 && height > 0)
                intrinsic = new Size(width, height);
        }

        return (
            intrinsic,
            string.IsNullOrWhiteSpace(metadata.Title) ? null : TrimMetadata(metadata.Title),
            string.IsNullOrWhiteSpace(metadata.Desc) ? null : TrimMetadata(metadata.Desc),
            contentHash,
            partitionHash);
    }

    /// <summary>The exact UTF-8 SHA-256 of a security partition.</summary>
    internal static string HashSecurityPartition(string? securityPartition) =>
        Utf8Sha256.GetHexString(securityPartition);

    /// <summary>Trims SVG metadata text and caps it at 4,096 characters.</summary>
    internal static string TrimMetadata(string value)
    {
        const int maximumLength = 4096;
        string trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength];
    }
}
