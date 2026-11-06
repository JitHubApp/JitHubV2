using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class BorrowedRasterSourceStreamTests
{
    [Fact]
    public async Task ReadOnlyBorrowedSourceSupportsSeekAndReadWithoutCloning()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        using var source = new MemoryStream(bytes, writable: false);
        using IRandomAccessStream stream = source.AsRandomAccessStream();

        Assert.Equal((ulong)bytes.Length, stream.Size);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        // The platform adapter supports seek/read, not cloning. WIC and
        // Win2D integration tests exercise their actual decoder requirements.
        Assert.Throws<NotSupportedException>(() => stream.GetInputStreamAt(2));
        Assert.Throws<NotSupportedException>(() => stream.CloneStream());
        byte[] first = new byte[2];
        byte[] second = new byte[2];
        stream.Seek(2);
        await stream.ReadAsync(first.AsBuffer(), 2, InputStreamOptions.None);
        stream.Seek(4);
        await stream.ReadAsync(second.AsBuffer(), 2, InputStreamOptions.None);
        Assert.Equal(new byte[] { 3, 4 }, first);
        Assert.Equal(new byte[] { 5, 6 }, second);
        Assert.Equal(6UL, stream.Position);
        Assert.Throws<NotSupportedException>(() => source.WriteByte(99));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, bytes);
    }

    [Fact]
    public void OpeningMaximumAdmittedSourceDoesNotAllocateAnotherManagedSourceBuffer()
    {
        // Warm the platform adapter before measuring only its managed opening
        // cost. This does not claim a WIC transient/native memory guarantee.
        using (var warmSource = new MemoryStream(new byte[1], writable: false))
        using (IRandomAccessStream warmStream = warmSource.AsRandomAccessStream())
            Assert.Equal(1UL, warmStream.Size);

        byte[] bytes = new byte[32 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var source = new MemoryStream(bytes, writable: false);
        using IRandomAccessStream stream = source.AsRandomAccessStream();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((ulong)bytes.Length, stream.Size);
        Assert.InRange(allocated, 0, 64 * 1024);
        GC.KeepAlive(bytes);
    }
}
