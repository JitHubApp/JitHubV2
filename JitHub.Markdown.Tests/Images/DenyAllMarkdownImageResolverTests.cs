using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.Markdown.Tests.Images;

/// <summary>
/// Covers how MarkdownViewer obtains its image resolver: it always ends with a
/// resolver that owns every source, so a broken registration never falls back to
/// the renderer's own URI loading.
/// </summary>
public sealed class DenyAllMarkdownImageResolverTests
{
    [Fact]
    public void ProviderFailureInstallsTheDenyAllResolver()
    {
        Assert.Same(
            DenyAllMarkdownImageResolver.Instance,
            DenyAllMarkdownImageResolver.CreateOrDenyAll(static () => throw new InvalidOperationException("no provider")));
    }

    [Fact]
    public void MissingProviderInstallsTheDenyAllResolver()
    {
        Assert.Same(
            DenyAllMarkdownImageResolver.Instance,
            DenyAllMarkdownImageResolver.CreateOrDenyAll(static () => null));
    }

    [Fact]
    public void WorkingProviderIsUsedAsIs()
    {
        RecordingResolver resolver = new();

        Assert.Same(resolver, DenyAllMarkdownImageResolver.CreateOrDenyAll(() => resolver));
    }

    private sealed class RecordingResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.RemoteContentBlocked));
    }
}
