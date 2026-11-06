using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;
using JitHub.Models.GitHub;

namespace JitHub.Services.Markdown;

/// <summary>
/// Adapts the app's GitHub client to the REST reads Markdown image resolution
/// uses, copying the encoded payload fields out of the app's response models.
/// </summary>
internal sealed class GitHubMarkdownContentClient : IGitHubMarkdownContentClient
{
    private readonly IGitHubClientService _client;

    /// <param name="client">The app's GitHub client.</param>
    public GitHubMarkdownContentClient(IGitHubClientService client)
    {
        _client = client;
    }

    public Task<string> GetRenderedReadmeHtmlAsync(
        string token,
        string owner,
        string name,
        string? gitRef,
        CancellationToken cancellationToken) =>
        _client.GetRenderedReadmeHtmlAsync(token, owner, name, gitRef, cancellationToken);

    public async Task<GitHubMarkdownEncodedContent> GetRepositoryFileAsync(
        string token,
        string owner,
        string name,
        string path,
        string? gitRef,
        CancellationToken cancellationToken)
    {
        GitHubRepositoryContent file = await _client.GetRepositoryContentAsync(
            token, owner, name, path, gitRef, cancellationToken).ConfigureAwait(false);
        return new GitHubMarkdownEncodedContent(file.Content, file.Encoding, file.Sha);
    }

    public async Task<GitHubMarkdownEncodedContent> GetBlobAsync(
        string token,
        string owner,
        string name,
        string sha,
        CancellationToken cancellationToken)
    {
        GitHubBlob blob = await _client.GetBlobAsync(
            token, owner, name, sha, cancellationToken).ConfigureAwait(false);
        return new GitHubMarkdownEncodedContent(blob.Content, blob.Encoding, blob.Sha);
    }
}
