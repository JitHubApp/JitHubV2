using System;
using System.IO;

namespace JitHub.Services.Markdown;

/// <summary>
/// Keeps the source-admission and image paths on one validated, process-local
/// same-byte fixture. This is used only by the explicit production audit mode.
/// </summary>
internal sealed class MarkdownSameByteAuditResolverRegistry
{
    private readonly object _gate = new();
    private Entry? _entry;

    internal MarkdownSameByteAuditImageResolver GetOrCreate(
        string corpusDirectory,
        string repository,
        string commit,
        string readmeGitBlobSha1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusDirectory);
        string fullPath = Path.GetFullPath(corpusDirectory);

        lock (_gate)
        {
            if (_entry is { } existing)
            {
                if (!string.Equals(existing.CorpusDirectory, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.Repository, repository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.Commit, commit, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.ReadmeGitBlobSha1, readmeGitBlobSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Same-byte audit fixture identity changed during this launch.");
                }

                return existing.Resolver;
            }

            var resolver = new MarkdownSameByteAuditImageResolver(
                fullPath, repository, commit, readmeGitBlobSha1);
            _entry = new Entry(fullPath, repository, commit, readmeGitBlobSha1, resolver);
            return resolver;
        }
    }

    internal void Clear()
    {
        lock (_gate)
            _entry = null;
    }

    private sealed record Entry(
        string CorpusDirectory,
        string Repository,
        string Commit,
        string ReadmeGitBlobSha1,
        MarkdownSameByteAuditImageResolver Resolver);
}
