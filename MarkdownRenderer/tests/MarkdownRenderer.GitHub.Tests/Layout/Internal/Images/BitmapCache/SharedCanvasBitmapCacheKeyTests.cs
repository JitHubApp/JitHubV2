using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;

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

        var original = new SharedCanvasBitmapCacheKey(firstDevice, identity);
        var sameDeviceAndValue = new SharedCanvasBitmapCacheKey(firstDevice, equalIdentity);
        var otherDevice = new SharedCanvasBitmapCacheKey(secondDevice, identity);
        var differentCase = new SharedCanvasBitmapCacheKey(firstDevice, differentCaseIdentity);

        Assert.NotSame(identity, equalIdentity);
        Assert.True(original.Equals(sameDeviceAndValue));
        Assert.Equal(original.GetHashCode(), sameDeviceAndValue.GetHashCode());
        Assert.False(original.Equals(otherDevice));
        Assert.False(original.Equals(differentCase));

        // Exercise the actual key behavior used by the generic LRU dictionary;
        // hash collisions remain legal, while distinct device/string components
        // must still occupy separate entries.
        var entries = new Dictionary<SharedCanvasBitmapCacheKey, string>
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
        SharedCanvasBitmapCacheKey key = default;

        int hashCode = key.GetHashCode();
        Assert.Equal(hashCode, key.GetHashCode());

        var entries = new Dictionary<SharedCanvasBitmapCacheKey, int>
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
        var storedKey = new SharedCanvasBitmapCacheKey(null!, identity);
        var entries = new Dictionary<SharedCanvasBitmapCacheKey, int>(capacity: 1)
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
        var failures = new ImageFailureCache();
        string acceptedKey = CreateUniqueKey(4_096);
        string rejectedKey = CreateUniqueKey(4_097);

        Assert.False(failures.HasFailedUrl(acceptedKey));
        Assert.False(failures.HasFailedUrl(rejectedKey));

        failures.RememberFailedUrl(acceptedKey);
        failures.RememberFailedUrl(rejectedKey);
        failures.RememberFailedUrl(string.Empty);

        Assert.True(failures.HasFailedUrl(acceptedKey));
        Assert.False(failures.HasFailedUrl(rejectedKey));
        Assert.False(failures.HasFailedUrl(string.Empty));
    }

    private static CanvasDevice CreateIdentityOnlyDevice()
    {
        var identityDevice = (CanvasDevice)RuntimeHelpers.GetUninitializedObject(typeof(CanvasDevice));
        GC.SuppressFinalize(identityDevice);
        return identityDevice;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MeasureDictionaryLookups(
        Dictionary<SharedCanvasBitmapCacheKey, int> entries,
        string lookupIdentity,
        int lookupCount)
    {
        int hits = 0;
        for (int i = 0; i < lookupCount; i++)
        {
            if (entries.ContainsKey(new SharedCanvasBitmapCacheKey(null!, lookupIdentity)))
                hits++;
        }
        return hits;
    }

    private static string CreateUniqueKey(int length)
    {
        string prefix = "negative-cache-test:" + Guid.NewGuid().ToString("N");
        if (prefix.Length >= length)
            throw new ArgumentOutOfRangeException(nameof(length));
        return prefix + new string('x', length - prefix.Length);
    }
}
