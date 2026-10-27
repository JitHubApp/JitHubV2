using System;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// One selected GitHub image result together with the owner for its borrowed source bytes.
/// Candidates are disposed when abandoned and detached only when transferred to the renderer.
/// </summary>
internal sealed partial class GitHubMarkdownImageCandidate : IDisposable
{
    private MarkdownImageResolution? _resolution;
    private IDisposable? _sourceOwner;

    private GitHubMarkdownImageCandidate(
        MarkdownImageResolution resolution,
        IDisposable? sourceOwner)
    {
        _resolution = resolution;
        _sourceOwner = sourceOwner;
    }

    internal MarkdownImageResolution Resolution => _resolution ?? throw new ObjectDisposedException(
        nameof(GitHubMarkdownImageCandidate));

    internal static GitHubMarkdownImageCandidate Unowned(MarkdownImageResolution resolution) =>
        new(resolution, sourceOwner: null);

    internal static GitHubMarkdownImageCandidate Resolved(
        MarkdownImageAsset asset,
        IDisposable? sourceOwner) =>
        new(MarkdownImageResolution.Resolved(asset), sourceOwner);

    internal MarkdownImageResolutionLease DetachLease()
    {
        MarkdownImageResolution resolution = Resolution;
        IDisposable? owner = _sourceOwner;
        var lease = new MarkdownImageResolutionLease(resolution, owner);
        _sourceOwner = null;
        _resolution = null;
        return lease;
    }

    public void Dispose()
    {
        _resolution = null;
        IDisposable? owner = _sourceOwner;
        _sourceOwner = null;
        owner?.Dispose();
    }
}
