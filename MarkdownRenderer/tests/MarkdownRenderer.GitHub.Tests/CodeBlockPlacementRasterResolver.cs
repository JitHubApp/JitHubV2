using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Resolves every image to one PNG payload and counts resolver calls.</summary>
internal sealed class CodeBlockPlacementRasterResolver(byte[] png) : IMarkdownImageResolver
{
    private readonly string _cacheKey = "code-placement:" + Guid.NewGuid().ToString("N");
    private int _resolveCount;

    internal int ResolveCount => Volatile.Read(ref _resolveCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _resolveCount);
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(
            new MarkdownImageAsset(png, "image/png", CacheKey: _cacheKey + source)));
    }
}
