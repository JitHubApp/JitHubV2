using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SharedCanvasBitmapCacheKeyTests
{
    [Fact]
    public void KeyPartitionsByDeviceReferenceAndComparesIdentityOrdinally()
    {
        CanvasDevice firstDevice = CreateIdentityOnlyDevice();
        CanvasDevice secondDevice = CreateIdentityOnlyDevice();
        const string identity = "svg:document:96x48";
        string equalIdentity = new(identity.ToCharArray());
        string differentCaseIdentity = identity.ToUpperInvariant();

        var original = new SharedCanvasBitmapCache.CacheKey(firstDevice, identity);
        var sameDeviceAndValue = new SharedCanvasBitmapCache.CacheKey(firstDevice, equalIdentity);
        var otherDevice = new SharedCanvasBitmapCache.CacheKey(secondDevice, identity);
        var differentCase = new SharedCanvasBitmapCache.CacheKey(firstDevice, differentCaseIdentity);

        Assert.NotSame(identity, equalIdentity);
        Assert.True(original.Equals(sameDeviceAndValue));
        Assert.Equal(original.GetHashCode(), sameDeviceAndValue.GetHashCode());
        Assert.False(original.Equals(otherDevice));
        Assert.False(original.Equals(differentCase));

        // Exercise the actual key behavior used by the generic LRU dictionary;
        // hash collisions remain legal, while distinct device/string components
        // must still occupy separate entries.
        var entries = new Dictionary<SharedCanvasBitmapCache.CacheKey, string>
        {
            [original] = "original",
            [otherDevice] = "other device",
            [differentCase] = "different case",
        };

        Assert.Equal(3, entries.Count);
        Assert.Equal("original", entries[sameDeviceAndValue]);
        Assert.Equal("other device", entries[otherDevice]);
        Assert.Equal("different case", entries[differentCase]);

        GC.KeepAlive(firstDevice);
        GC.KeepAlive(secondDevice);
    }

    [Fact]
    public void DefaultKeyHasStableHashAndWorksInDictionary()
    {
        SharedCanvasBitmapCache.CacheKey key = default;

        int hashCode = key.GetHashCode();
        Assert.Equal(hashCode, key.GetHashCode());

        var entries = new Dictionary<SharedCanvasBitmapCache.CacheKey, int>
        {
            [key] = 42,
        };

        Assert.Equal(42, entries[key]);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void WarmDictionaryLookupCreatesNoPerProbeKeyAllocation(int lookupCount)
    {
        const string identity = "svg:allocation-probe:1024x512";
        string lookupIdentity = new(identity.ToCharArray());
        var storedKey = new SharedCanvasBitmapCache.CacheKey(null!, identity);
        var entries = new Dictionary<SharedCanvasBitmapCache.CacheKey, int>(capacity: 1)
        {
            [storedKey] = 1,
        };

        // Warm the exact no-inline loop and lookup call site at full length so
        // tiered compilation/OSR cannot pollute the measured interval. Null is
        // an identity-only device token here: no Win2D or GPU is initialized.
        int warmHits = MeasureDictionaryLookups(entries, lookupIdentity, lookupCount);
        warmHits += MeasureDictionaryLookups(entries, lookupIdentity, lookupCount);
        warmHits += MeasureDictionaryLookups(entries, lookupIdentity, lookupCount);
        Assert.Equal(lookupCount * 3, warmHits);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int hits = MeasureDictionaryLookups(entries, lookupIdentity, lookupCount);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(lookupCount, hits);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void FailedUrlMemoizationEnforcesTheExactKeyLengthBoundary()
    {
        ConcurrentDictionary<string, byte> failedUrls = FailedUrlCache();
        string acceptedKey = CreateUniqueKey(4_096);
        string rejectedKey = CreateUniqueKey(4_097);
        bool emptyKeyWasPresent = failedUrls.ContainsKey(string.Empty);

        Assert.False(failedUrls.ContainsKey(acceptedKey));
        Assert.False(failedUrls.ContainsKey(rejectedKey));

        try
        {
            RememberFailedUrl(acceptedKey);
            RememberFailedUrl(rejectedKey);
            RememberFailedUrl(string.Empty);

            Assert.True(failedUrls.ContainsKey(acceptedKey));
            Assert.False(failedUrls.ContainsKey(rejectedKey));
            Assert.Equal(emptyKeyWasPresent, failedUrls.ContainsKey(string.Empty));
        }
        finally
        {
            // These keys are unique to this test. Do not reset the process-wide
            // cache or remove entries written by concurrently running tests.
            _ = failedUrls.TryRemove(acceptedKey, out _);
            _ = failedUrls.TryRemove(rejectedKey, out _);
        }
    }

    [Fact]
    public void SvgFailureMemoizationTrimsDescriptionsAndAllFailureWritesAreBounded()
    {
        string source = File.ReadAllText(FindImageBoxSource());
        string failurePublisher = ExtractBetween(
            source,
            "private void PublishSvgFailure(",
            "private static string TrimSvgMetadata(");

        Assert.Contains("private const int MaxFailedUrlEntrnes = 512;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxFailedCacheKeyLength = 4096;", source, StringComparison.Ordinal);
        Assert.Contains("failureCacheKey.Length is > 0 and <= MaxFailedCacheKeyLength", failurePublisher, StringComparison.Ordinal);
        Assert.Contains(
            "_svgFailures[failureCacheKey] = (reason, TrimSvgMetadata(description));",
            failurePublisher,
            StringComparison.Ordinal);

        string failedUrlHelper = ExtractBetween(
            source,
            "private static void RememberFailedUrl(string cacheKey)",
            "private static void TrimCache<TValue>");
        Assert.Contains("cacheKey.Length is 0 or > MaxFailedCacheKeyLength", failedUrlHelper, StringComparison.Ordinal);
        Assert.Contains("_failedUrls.TryAdd(cacheKey, 0);", failedUrlHelper, StringComparison.Ordinal);

        // The sole failed-URL insertion belongs to the guarded helper, and all
        // transport failure publication sites route through that helper.
        Assert.Single(Regex.Matches(
            source,
            @"_failedUrls\s*(?:\.\s*(?:TryAdd|GetOrAdd|AddOrUpdate|TryUpdate)\s*\(|\[\s*)").Cast<Match>());
        Assert.Equal(3, CountOccurrences(source, "RememberFailedUrl(cacheKey);"));
    }

    private static CanvasDevice CreateIdentityOnlyDevice()
    {
        var identityDevice = (CanvasDevice)RuntimeHelpers.GetUninitializedObject(typeof(CanvasDevice));
        GC.SuppressFinalize(identityDevice);
        return identityDevice;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MeasureDictionaryLookups(
        Dictionary<SharedCanvasBitmapCache.CacheKey, int> entries,
        string lookupIdentity,
        int lookupCount)
    {
        int hits = 0;
        for (int i = 0; i < lookupCount; i++)
        {
            if (entries.ContainsKey(new SharedCanvasBitmapCache.CacheKey(null!, lookupIdentity)))
                hits++;
        }
        return hits;
    }

    private static ConcurrentDictionary<string, byte> FailedUrlCache()
    {
        FieldInfo field = typeof(ImageBox).GetField(
            "_failedUrls",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ImageBox).FullName, "_failedUrls");
        return (ConcurrentDictionary<string, byte>)field.GetValue(null)!;
    }

    private static void RememberFailedUrl(string cacheKey)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "RememberFailedUrl",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "RememberFailedUrl");
        _ = method.Invoke(null, [cacheKey]);
    }

    private static string CreateUniqueKey(int length)
    {
        string prefix = "negative-cache-test:" + Guid.NewGuid().ToString("N");
        if (prefix.Length >= length)
            throw new ArgumentOutOfRangeException(nameof(length));
        return prefix + new string('x', length - prefix.Length);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = start < 0
            ? -1
            : source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate '{startMarker}' and its end marker.");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string FindImageBoxSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException("Could not locate ImageBox.cs from the test base directory.");
    }
}
