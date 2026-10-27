using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class SvgBitmapAliasCacheTests
{
    [Fact]
    public void LateBitmapMetadataDoesNotReplaceNewerUrlAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string urlKey = "https://cache-alias.test/" + suffix;
        string contentKey = "svg-content:test:" + suffix;
        object oldContent = CreateEntry([1, 2, 3], "old-hash", "old-partition", 17, 23, "old");
        object newerAlias = CreateEntry([4, 5, 6], "new-hash", "new-partition", 17, 23, "new");
        try
        {
            SetEntry(contentKey, oldContent);
            SetEntry(urlKey, newerAlias);
            Update(urlKey, contentKey, "old-bitmap", 80, 40);

            Assert.Same(newerAlias, GetEntry(urlKey));
            object updatedContent = Assert.IsAssignableFrom<object>(GetEntry(contentKey));
            Assert.NotSame(oldContent, updatedContent);
            Assert.Equal("old-bitmap", GetField<string?>(updatedContent, "LastBitmapIdentity"));
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
        object content = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "content");
        try
        {
            SetEntry(contentKey, content);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            Assert.Null(GetEntry(urlKey));
            object updatedContent = Assert.IsAssignableFrom<object>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Equal("bitmap", GetField<string?>(updatedContent, "LastBitmapIdentity"));
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
        object shared = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "title");
        try
        {
            SetEntry(contentKey, shared);
            SetEntry(urlKey, shared);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            object updatedContent = Assert.IsAssignableFrom<object>(GetEntry(contentKey));
            object updatedAlias = Assert.IsAssignableFrom<object>(GetEntry(urlKey));
            Assert.NotSame(shared, updatedContent);
            Assert.Same(updatedContent, updatedAlias);
            Assert.Same(GetField<byte[]>(shared, "RawBytes"), GetField<byte[]>(updatedContent, "RawBytes"));
            Assert.Equal("bitmap", GetField<string?>(updatedContent, "LastBitmapIdentity"));
            Assert.Equal(80, GetField<int>(updatedContent, "LastBitmapWidthPixels"));
            Assert.Equal(40, GetField<int>(updatedContent, "LastBitmapHeightPixels"));
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
        object content = CreateEntry(sameRawBytes, "hash", "partition", 17, 23, "content-title");
        object alias = CreateEntry(sameRawBytes, "hash", "partition", 17, 23, "alias-title");
        try
        {
            SetEntry(contentKey, content);
            SetEntry(urlKey, alias);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            object updatedContent = Assert.IsAssignableFrom<object>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Same(alias, GetEntry(urlKey));
            Assert.Equal("bitmap", GetField<string?>(updatedContent, "LastBitmapIdentity"));
            Assert.Null(GetField<string?>(alias, "LastBitmapIdentity"));
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
        object shared = CreateEntry([1, 2, 3], "hash", "partition", 17, 23, "title");
        try
        {
            SetEntry(contentKey, shared);
            SetEntry(urlKey, shared);
            RemoveEntry(contentKey);
            Update(urlKey, contentKey, "bitmap", 80, 40);

            Assert.Null(GetEntry(contentKey));
            Assert.Same(shared, GetEntry(urlKey));
            Assert.Null(GetField<string?>(shared, "LastBitmapIdentity"));
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
        object content = CreateEntry([1, 2, 3], "content-hash", "partition", 17, 23, "content");
        object resolverAlias = CreateEntry([4, 5, 6], "resolver-hash", "partition", 17, 23, "resolver");
        try
        {
            SetEntry(contentKey, content);
            SetEntry(resolverKey, resolverAlias);
            Assert.Null(GetEntry(candidateBuiltInAliasKey));

            Update(null, contentKey, "bitmap", 80, 40);

            object updatedContent = Assert.IsAssignableFrom<object>(GetEntry(contentKey));
            Assert.NotSame(content, updatedContent);
            Assert.Equal("bitmap", GetField<string?>(updatedContent, "LastBitmapIdentity"));
            Assert.Equal(80, GetField<int>(updatedContent, "LastBitmapWidthPixels"));
            Assert.Equal(40, GetField<int>(updatedContent, "LastBitmapHeightPixels"));
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

    private static object CreateEntry(
        byte[] rawBytes,
        string contentHash,
        string partitionHash,
        long rendererIdentity,
        long providerCacheGeneration,
        string title)
    {
        Type entryType = typeof(ImageBox).GetNestedType("SvgCacheEntry", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = Assert.Single(entryType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        return constructor.Invoke(
        [
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
            null,
        ]);
    }

    private static void Update(
        string? builtInSourceAliasKey,
        string contentKey,
        string bitmapIdentity,
        int width,
        int height)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "UpdateSvgSourceCacheBitmap",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [builtInSourceAliasKey, contentKey, bitmapIdentity, width, height, null]);
    }

    private static void SetEntry(string key, object entry)
    {
        object cache = GetCache();
        cache.GetType().GetMethod("Set", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(cache, [key, entry]);
    }

    private static object? GetEntry(string key)
    {
        object cache = GetCache();
        object?[] arguments = [key, null];
        bool found = Assert.IsType<bool>(cache.GetType()
            .GetMethod("TryGetValue", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(cache, arguments));
        return found ? arguments[1] : null;
    }

    private static void RemoveEntry(string key)
    {
        object cache = GetCache();
        _ = cache.GetType().GetMethod("Remove", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(cache, [key]);
    }

    private static object GetCache() => typeof(ImageBox)
        .GetField("_svgCache", BindingFlags.Static | BindingFlags.NonPublic)!
        .GetValue(null)!;

    private static T GetField<T>(object instance, string fieldName) =>
        (T)instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
}
