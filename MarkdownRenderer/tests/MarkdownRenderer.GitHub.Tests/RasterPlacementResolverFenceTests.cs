using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RasterPlacementResolverFenceTests
{
    [Fact]
    public void ScopedResolverMatchesCurrentPublicResolverSessionAndContext()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();
        using IMarkdownPerformanceDocumentScope scope = api.OpenDocument(resolver, context);

        Assert.True(Match(resolver, scope, api, api, context, context));
    }

    [Fact]
    public void ScopedResolverRejectsReplacementPublicResolver()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var capturedResolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();
        using IMarkdownPerformanceDocumentScope scope = api.OpenDocument(capturedResolver, context);

        Assert.False(Match(new NoIoResolver(), scope, api, api, context, context));
    }

    [Fact]
    public void ScopedResolverRejectsDifferentSession()
    {
        using var capturedSession = CreateSession();
        using var currentSession = CreateSession();
        IMarkdownPerformanceSessionInternal capturedApi = capturedSession;
        IMarkdownPerformanceSessionInternal currentApi = currentSession;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();
        using IMarkdownPerformanceDocumentScope scope = capturedApi.OpenDocument(resolver, context);

        Assert.False(Match(resolver, scope, currentApi, capturedApi, context, context));
    }

    [Fact]
    public void ScopedResolverRejectsDifferentResolveContext()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext captured = CreateContext();
        using IMarkdownPerformanceDocumentScope scope = api.OpenDocument(resolver, captured);
        MarkdownImageResolveContext current = captured with { DocumentPath = "docs/other.md" };

        Assert.False(Match(resolver, scope, api, api, current, captured));
    }

    [Fact]
    public void ScopedResolverRejectsDisposedScope()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();
        IMarkdownPerformanceDocumentScope scope = api.OpenDocument(resolver, context);
        try
        {
            scope.Dispose();

            Assert.False(Match(resolver, scope, api, api, context, context));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ScopedResolverRejectsDisposedSession()
    {
        var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();
        IMarkdownPerformanceDocumentScope scope = api.OpenDocument(resolver, context);
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(api.IsDisposed);
            Assert.False(Match(resolver, scope, api, api, context, context));
        }
        finally
        {
            scope.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void BareResolverAcceptsSamePublicResolverReference()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var resolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();

        Assert.True(Match(resolver, resolver, api, api, context, context));
    }

    [Fact]
    public void BareResolverRejectsReplacementPublicResolver()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        var capturedResolver = new NoIoResolver();
        MarkdownImageResolveContext context = CreateContext();

        Assert.False(Match(new NoIoResolver(), capturedResolver, api, api, context, context));
    }

    [Fact]
    public void BuiltInUriResolverAllowsNullPairWithLiveSession()
    {
        using var session = CreateSession();
        IMarkdownPerformanceSessionInternal api = session;
        MarkdownImageResolveContext context = CreateContext();

        Assert.True(Match(null, null, api, api, context, context));
    }

    [Fact]
    public void BuiltInUriResolverAllowsNullPairWithoutSession()
    {
        MarkdownImageResolveContext context = CreateContext();

        Assert.True(Match(null, null, null, null, context, context));
    }

    private static bool Match(
        IMarkdownImageResolver? currentPublicResolver,
        IMarkdownImageResolver? capturedEffectiveResolver,
        IMarkdownPerformanceSessionInternal? currentSession,
        IMarkdownPerformanceSessionInternal? capturedSession,
        MarkdownImageResolveContext currentContext,
        MarkdownImageResolveContext capturedContext) =>
        RasterPlacementResolverFence.Matches(
            currentPublicResolver,
            capturedEffectiveResolver,
            currentSession,
            capturedSession,
            currentContext,
            capturedContext);

    private static MarkdownPerformanceSession CreateSession() =>
        new(new MarkdownPerformanceOptions());

    private static MarkdownImageResolveContext CreateContext() =>
        new(
            new Uri("https://example.test/README.md"),
            "README.md",
            false,
            new MarkdownDocumentSource("repo:owner/name@main:README.md", "owner", "name", "main", "README.md"));

    private sealed class NoIoResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);
    }
}
