using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Caches GitHub's server-rendered README source-to-Camo image maps for the
/// current account. Its owner clears it whenever the account credential changes.
/// </summary>
internal sealed class GitHubRenderedReadmeImageMaps
{
    private readonly IGitHubClientService _gitHubClientService;
    private readonly Func<string> _getAccessToken;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, string>>>
        _renderedReadmeImageMaps = new(StringComparer.Ordinal);

    internal GitHubRenderedReadmeImageMaps(
        IGitHubClientService gitHubClientService,
        Func<string> getAccessToken)
    {
        _gitHubClientService = gitHubClientService;
        _getAccessToken = getAccessToken;
    }

    internal void Clear() => _renderedReadmeImageMaps.Clear();

    internal async Task<IReadOnlyDictionary<string, string>> GetAsync(
        MarkdownDocumentSource document,
        CancellationToken cancellationToken)
    {
        string key = $"{document.Owner}/{document.Repository}/{document.Ref}/{document.Path}";
        Task<IReadOnlyDictionary<string, string>> task = _renderedReadmeImageMaps.GetOrAdd(
            key,
            _ => LoadRenderedReadmeImageMapAsync(document, cancellationToken));
        try
        {
            IReadOnlyDictionary<string, string> result = await task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            TrimRenderedReadmeImageMaps(key);
            return result;
        }
        catch
        {
            _renderedReadmeImageMaps.TryRemove(
                new KeyValuePair<string, Task<IReadOnlyDictionary<string, string>>>(key, task));
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadRenderedReadmeImageMapAsync(
        MarkdownDocumentSource document,
        CancellationToken cancellationToken)
    {
        string html = await _gitHubClientService.GetRenderedReadmeHtmlAsync(
            _getAccessToken(),
            document.Owner!,
            document.Repository!,
            document.Ref,
            cancellationToken).ConfigureAwait(false);
        return GitHubCamoImageMapParser.Parse(html);
    }

    private void TrimRenderedReadmeImageMaps(string retainedKey)
    {
        const int entryBudget = 32;
        if (_renderedReadmeImageMaps.Count <= entryBudget)
            return;

        foreach (KeyValuePair<string, Task<IReadOnlyDictionary<string, string>>> pair in
                 _renderedReadmeImageMaps)
        {
            if (_renderedReadmeImageMaps.Count <= entryBudget)
                break;
            if (!pair.Key.Equals(retainedKey, StringComparison.Ordinal) && pair.Value.IsCompleted)
            {
                _renderedReadmeImageMaps.TryRemove(pair);
            }
        }
    }
}
