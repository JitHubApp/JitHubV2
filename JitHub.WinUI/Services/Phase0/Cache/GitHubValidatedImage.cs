using System.IO;

namespace JitHub.Services;

/// <summary>
/// Downloaded image bytes whose size and signature passed admission, with the
/// media type detected from those bytes.
/// </summary>
internal readonly record struct GitHubValidatedImage(byte[] Bytes, string ContentType)
{
    /// <summary>Validates a fresh download or throws <see cref="InvalidDataException"/>.</summary>
    internal static GitHubValidatedImage From(GitHubImageDownload? download)
    {
        if (download?.Bytes is not { Length: > 0 } bytes ||
            bytes.Length > GitHubImageService.MaxImageBytes ||
            !GitHubImageContentSniffer.TryDetectSupportedImageContentType(bytes, download.ContentType, out string contentType))
        {
            throw new InvalidDataException("Remote content is not a supported image.");
        }

        return new GitHubValidatedImage(bytes, contentType);
    }
}
