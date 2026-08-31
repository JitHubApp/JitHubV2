using System;
using System.Globalization;
using System.Threading;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GistSynchronizationGate
{
    private int _generation;

    public int Capture() => Volatile.Read(ref _generation);

    public int Invalidate() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);
}

public static class GistFileContentPolicy
{
    public static string GetPreviewText(GitHubGistFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Truncated && string.IsNullOrEmpty(file.Content)
            ? "GitHub did not include this large file in the gist response."
            : file.Content ?? "Select the file again after the gist finishes loading.";
    }

    public static string GetTruncationMessage(GitHubGistFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Truncated
            ? "This preview is incomplete. Load the full file to inspect all content."
            : string.Empty;
    }
}

public sealed record GistFileRenderModel(
    string PreviewText,
    bool IsCapped,
    int FullCharacterCount,
    string StatusText);

public static class GistFileRenderPolicy
{
    public const int MaximumPreviewCharacters = 64 * 1024;

    public static GistFileRenderModel Create(string? content)
    {
        string value = content ?? string.Empty;
        if (value.Length <= MaximumPreviewCharacters)
        {
            return new GistFileRenderModel(value, IsCapped: false, value.Length, string.Empty);
        }

        int previewLength = MaximumPreviewCharacters;
        if (char.IsHighSurrogate(value[previewLength - 1]) && char.IsLowSurrogate(value[previewLength]))
        {
            previewLength--;
        }

        string status = string.Format(
            CultureInfo.CurrentCulture,
            "Showing the first {0:N0} of {1:N0} characters. Copy and Save as use the complete file.",
            previewLength,
            value.Length);
        return new GistFileRenderModel(value[..previewLength], IsCapped: true, value.Length, status);
    }
}
