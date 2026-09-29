using System;

namespace MarkdownRenderer.Images;

internal enum MarkdownBuiltInImageSourceKind
{
    Data,
    Local,
    RemoteHttps,
    InsecureHttp,
    Unsupported,
}

internal readonly record struct MarkdownBuiltInImageSource(
    Uri Uri,
    string CacheKey,
    MarkdownBuiltInImageSourceKind Kind)
{
    public bool CanLoadWithoutResolver =>
        Kind is MarkdownBuiltInImageSourceKind.Data or MarkdownBuiltInImageSourceKind.Local;
}

/// <summary>
/// Resolves public image sources without granting network access. The returned
/// absolute URI is also the only safe built-in cache identity for a relative
/// source: the same relative path under two document bases must not alias.
/// </summary>
internal static class MarkdownBuiltInImageSourcePolicy
{
    private const string MsAppxScheme = "ms-appx";
    private const string MsAppDataScheme = "ms-appdata";
    private const string DataScheme = "data";

    public static bool TryResolve(
        string? source,
        Uri? baseUri,
        out MarkdownBuiltInImageSource resolved)
    {
        resolved = default;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        Uri? absoluteUri = null;
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? parsed) && parsed.IsAbsoluteUri)
        {
            absoluteUri = parsed;
        }
        else if (baseUri is { IsAbsoluteUri: true } &&
                 Uri.TryCreate(baseUri, source, out Uri? combined) &&
                 combined.IsAbsoluteUri)
        {
            absoluteUri = combined;
        }

        if (absoluteUri is null)
            return false;

        MarkdownBuiltInImageSourceKind kind = Classify(absoluteUri);
        resolved = new MarkdownBuiltInImageSource(
            absoluteUri,
            absoluteUri.AbsoluteUri,
            kind);
        return true;
    }

    private static MarkdownBuiltInImageSourceKind Classify(Uri uri)
    {
        if (uri.Scheme.Equals(DataScheme, StringComparison.OrdinalIgnoreCase))
            return MarkdownBuiltInImageSourceKind.Data;

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return MarkdownBuiltInImageSourceKind.InsecureHttp;

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return MarkdownBuiltInImageSourceKind.RemoteHttps;

        if (uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            // A file URI with a non-local host can trigger an implicit SMB request.
            // Never treat that as a local, built-in image source.
            return string.IsNullOrEmpty(uri.Host) || uri.IsLoopback
                ? MarkdownBuiltInImageSourceKind.Local
                : MarkdownBuiltInImageSourceKind.Unsupported;
        }

        return uri.Scheme.Equals(MsAppxScheme, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(MsAppDataScheme, StringComparison.OrdinalIgnoreCase)
            ? MarkdownBuiltInImageSourceKind.Local
            : MarkdownBuiltInImageSourceKind.Unsupported;
    }
}
