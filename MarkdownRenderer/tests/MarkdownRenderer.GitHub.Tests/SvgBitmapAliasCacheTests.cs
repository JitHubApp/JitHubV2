using MarkdownRenderer.Layout;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class SvgBitmapAliasCacheTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void LateBitmapMetadataDoesNotReplaceNewerUrlAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        SvgCacheEntry oldContent = CreateEntry([1, 2, 3], "old-hash", "old-partition", 17, 23, "old");
        SvgCacheEntry newerAlias = CreateEntry([4, 5, 6], "new-hash", "new-partition", 17, 23, "new");
        try
        {
            SetEntry(contentKey, oldContent);
            SetEntry(urlKey, newerAlias);
            Update(urlKey, contentKey, "old-bitmap", 80, 40);

            Assert.Same(newerAlias, GetEntry(urlKey));
            SvgCacheEntry updatedContent = Assert.IsType<SvgCacheEntry>(GetEntry(contentKey));
            Assert.NotSame(oldContent, updatedContent);
            Assert.Equal("old-bitmap", updatedContent.LastBitmapIdentity);
        }
        finally
        {
            RemoveEntry(urlKey);
            RemoveEntry(contentKey);
        }
    }

    [Fact]
    public void LateBitmapMetadataDoesNotResurrectEvictedUrlAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        SvgCacheEntry content = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "content");
        try
        {
            SetEntry(contentKey, content);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            Assert.Null(GetEntry(urlKey));
            SvgCacheEntry updatedContent = Assert.IsType<SvgCacheEntry>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Equal("bitmap", updatedContent.LastBitmapIdentity);
        }
        finally
        {
            RemoveEntry(urlKey);
            RemoveEntry(contentKey);
        }
    }

    [Fact]
    public void ExactSharedAliasAndContentOwnerReceiveBitmapMetadata()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        SvgCacheEntry shared = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "title");
        try
        {
            SetEntry(contentKey, shared);
            SetEntry(urlKey, shared);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            SvgCacheEntry updatedContent = Assert.IsType<SvgCacheEntry>(GetEntry(contentKey));
            SvgCacheEntry updatedAlias = Assert.IsType<SvgCacheEntry>(GetEntry(urlKey));
            Assert.NotSame(shared, updatedContent);
            Assert.Same(updatedContent, updatedAlias);
            Assert.Same(shared.RawBytes, updatedContent.RawBytes);
            Assert.Equal("bitmap", updatedContent.LastBitmapIdentity);
            Assert.Equal(80, updatedContent.LastBitmapWidthPixels);
            Assert.Equal(40, updatedContent.LastBitmapHeightPixels);
        }
        finally
        {
            RemoveEntry(urlKey);
            RemoveEntry(contentKey);
        }
    }

    [Fact]
    public void SameByteIdentityButDistinctAliasOwnerIsNotReplaced()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        byte[] sameRawBytes = [1, 2, 3];
        SvgCacheEntry content = CreateEntry(sameRawBytes, "hash", "partition", 17, 23, "content-title");
        SvgCacheEntry alias = CreateEntry(sameRawBytes, "hash", "partition", 17, 23, "alias-title");
        try
        {
            SetEntry(contentKey, content);
            SetEntry(urlKey, alias);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            SvgCacheEntry updatedContent = Assert.IsType<SvgCacheEntry>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Same(alias, GetEntry(urlKey));
            Assert.Equal("bitmap", updatedContent.LastBitmapIdentity);
            Assert.Null(alias.LastBitmapIdentity);
        }
        finally
        {
            RemoveEntry(urlKey);
            RemoveEntry(contentKey);
        }
    }

    [Fact]
    public void EvictedContentEntryDoesNotModifyTheRemainingAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        SvgCacheEntry shared = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "title");
        try
        {
            SetEntry(contentKey, shared);
            SetEntry(urlKey, shared);
            RemoveEntry(contentKey);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            Assert.Null(GetEntry(contentKey));
            Assert.Same(shared, GetEntry(urlKey));
            Assert.Null(shared.LastBitmapIdentity);
        }
        finally
        {
            RemoveEntry(urlKey);
            RemoveEntry(contentKey);
        }
    }

    [Fact]
    public void BitmapMetadataWithoutBuiltInAliasOnlyReplacesContent()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string resolverKey = "github:private-repository:" + suffix;
        string candidateBuiltInAliasKey = "file:///svg-alias-test/" + suffix + ".svg";
        string contentKey = "svg-content:test:" + suffix;
        SvgCacheEntry content = CreateEntry([1, 2, 3], "content-hash", "partition", 17, 23, "content");
        SvgCacheEntry resolverAlias = CreateEntry([4, 5, 6], "resolver-hash", "partition", 17, 23, "resolver");
        try
        {
            SetEntry(contentKey, content);
            SetEntry(resolverKey, resolverAlias);
            Assert.Null(GetEntry(candidateBuiltInAliasKey));

            Update(null, contentKey, "bitmap", 80, 40);

            SvgCacheEntry updatedContent = Assert.IsType<SvgCacheEntry>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Equal("bitmap", updatedContent.LastBitmapIdentity);
            Assert.Equal(80, updatedContent.LastBitmapWidthPixels);
            Assert.Equal(40, updatedContent.LastBitmapHeightPixels);
            Assert.Same(resolverAlias, GetEntry(resolverKey));
            Assert.Null(GetEntry(candidateBuiltInAliasKey));
        }
        finally
        {
            RemoveEntry(candidateBuiltInAliasKey);
            RemoveEntry(resolverKey);
            RemoveEntry(contentKey);
        }
    }

    private static SvgCacheEntry CreateEntry(
        byte[] rawBytes,
        string contentHash,
        string partitionHash,
        long rendererIdentity,
        long providerCacheGeneration,
        string title)
    {
        return new SvgCacheEntry(
            rawBytes,
            new Size(40, 20),
            title,
            null,
            contentHash,
            partitionHash,
            new MarkdownSvgDocumentInfo(40, 20),
            rendererIdentity,
            providerCacheGeneration,
            null,
            0,
            0,
            null);
    }

    private void Update(
        string? builtInSourceAliasKey,
        string contentKey,
        string bitmapIdentity,
        int width,
        int height)
    {
        _renderResources.Images.SvgSources.RecordLastBitmap(
            builtInSourceAliasKey, contentKey, bitmapIdentity, width, height, physicalViewport: null);
    }

    private void SetEntry(string key, SvgCacheEntry entry)
    {
        _renderResources.Images.SvgSources.Set(key, entry);
    }

    private SvgCacheEntry? GetEntry(string key)
    {
        return _renderResources.Images.SvgSources.TryGetValue(key, out SvgCacheEntry? entry) ? entry : null;
    }

    private void RemoveEntry(string key)
    {
        _ = _renderResources.Images.SvgSources.Remove(key);
    }
}
