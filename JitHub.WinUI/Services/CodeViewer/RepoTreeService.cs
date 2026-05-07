using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.CodeViewer;
using JitHub.Models.LegacyGitHub;

namespace JitHub.Services.CodeViewer;

public sealed class RepoTreeService : IRepoTreeService
{
    private readonly IGitHubService _gitHubService;

    public RepoTreeService(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    public Task<RepoTree> LoadTreeAsync(string owner, string name, string refOrSha, CancellationToken ct)
    {
        return _gitHubService.GetRepoTreeAsync(owner, name, refOrSha, ct);
    }

    public async Task<IReadOnlyList<RepoContentNode>> LoadDirectoryAsync(
        string owner,
        string name,
        string path,
        string refOrSha,
        CancellationToken ct)
    {
        ICollection<RepoContentNode> nodes = await _gitHubService.GetRepoContents(owner, name, path, refOrSha);
        return nodes is IReadOnlyList<RepoContentNode> list ? list : nodes.ToList();
    }

    public async Task<RepoFileBlob> LoadBlobAsync(string owner, string name, string sha, CancellationToken ct)
    {
        Blob blob = await _gitHubService.GetBlocFromGit(owner, name, sha);

        byte[] bytes = await Task.Run(() => DecodeBlob(blob.Content, blob.Encoding.Value), ct);

        bool isBinary = IsBinaryContent(bytes);
        string? text = isBinary ? null : DecodeText(bytes);

        return new RepoFileBlob
        {
            Sha = blob.Sha,
            Encoding = blob.Encoding.StringValue,
            Bytes = bytes,
            Text = text,
            IsBinary = isBinary,
        };
    }

    private static byte[] DecodeBlob(string? content, EncodingType encoding)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<byte>();

        if (encoding == EncodingType.Base64)
        {
            string normalized = content.Replace("\r", string.Empty).Replace("\n", string.Empty);
            return Convert.FromBase64String(normalized);
        }

        return Encoding.UTF8.GetBytes(content);
    }

    private static bool IsBinaryContent(byte[] bytes)
    {
        int scanLength = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < scanLength; i++)
        {
            if (bytes[i] == 0)
                return true;
        }
        return false;
    }

    private static string? DecodeText(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
