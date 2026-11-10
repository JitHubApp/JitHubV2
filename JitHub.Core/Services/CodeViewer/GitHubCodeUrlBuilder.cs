using System;
using System.Linq;

namespace JitHub.Services.CodeViewer;

public static class GitHubCodeUrlBuilder
{
    public static string BuildBlobUrl(
        string owner,
        string repository,
        string gitRef,
        string path) =>
        $"https://github.com/{EncodeSegment(owner)}/{EncodeSegment(repository)}/blob/{EncodeSegment(gitRef)}/{EncodePath(path)}";

    public static string BuildRawUrl(
        string owner,
        string repository,
        string gitRef,
        string path) =>
        $"https://raw.githubusercontent.com/{EncodeSegment(owner)}/{EncodeSegment(repository)}/{EncodeSegment(gitRef)}/{EncodePath(path)}";

    public static string BuildTreeUrl(
        string owner,
        string repository,
        string gitRef,
        string path)
    {
        string baseUrl = $"https://github.com/{EncodeSegment(owner)}/{EncodeSegment(repository)}/tree/{EncodeSegment(gitRef)}";
        string encodedPath = EncodePath(path);
        return encodedPath.Length == 0 ? baseUrl : $"{baseUrl}/{encodedPath}";
    }

    public static string AppendLineFragment(string url, int oneBasedLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (oneBasedLine < 1) throw new ArgumentOutOfRangeException(nameof(oneBasedLine));

        int fragment = url.IndexOf('#');
        string withoutFragment = fragment < 0 ? url : url[..fragment];
        return $"{withoutFragment}#L{oneBasedLine}";
    }

    public static string EncodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Trim('/');
        return string.Join(
            '/',
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(EncodeSegment));
    }

    private static string EncodeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return Uri.EscapeDataString(value);
    }
}
