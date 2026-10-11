using System;
using System.Threading.Tasks;
using JitHub.Services;

namespace JitHub.Services.Markdown;

/// <summary>
/// Transfers an LFS-media source lease through the image-service cache write without
/// copying its bytes or releasing its reservation early.
/// </summary>
internal static class GitHubLeasedImageDownloadTransfer
{
    internal static async Task<GitHubImageDownload?> ResolveLfsDownloadAsync(
        IDisposable? pointerReservation,
        Func<Task<GitHubImageSourceLease?>> resolveMedia)
    {
        ArgumentNullException.ThrowIfNull(resolveMedia);
        pointerReservation?.Dispose();
        GitHubImageSourceLease? sourceLease = await resolveMedia().ConfigureAwait(false);
        return ToDownload(sourceLease);
    }

    internal static GitHubImageDownload? ToDownload(GitHubImageSourceLease? sourceLease)
    {
        try
        {
            if (sourceLease is null)
                return null;

            GitHubCachedImage image = sourceLease.Image;
            if (image.Bytes is not { Length: > 0 } bytes)
                return null;

            var download = new GitHubImageDownload(
                bytes,
                image.ContentType,
                SourceByteLease: sourceLease);
            sourceLease = null;
            return download;
        }
        finally
        {
            sourceLease?.Dispose();
        }
    }
}
