using System.Runtime.CompilerServices;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;
using MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins the static snapshot's bounded segmented output: its exact byte
/// limit, cancellation, configuration ceiling, and that every segment is
/// cleared and released on dispose.
/// </summary>
public sealed class BoundedSegmentedWriteStreamTests
{
    [Fact]
    public void SegmentedOutputAcceptsExactLimitThenClearsEverySegmentOnDispose()
    {
        byte[] expected = Enumerable.Range(0, 32_771).Select(static value => (byte)value).ToArray();
        var allocated = new List<byte[]>();
        var output = new BoundedSegmentedWriteStream(expected.Length, CancellationToken.None, RecordSegments(allocated));
        output.Write(expected);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal((long)expected.Length, output.Length);

        output.Dispose();

        AssertSegmentsClearedAndReleased(output, allocated);
    }

    [Fact]
    public void SegmentedOutputRejectsFirstByteBeyondLimitWithoutGrowingAndClearsBuffers()
    {
        var allocated = new List<byte[]>();
        var output = new BoundedSegmentedWriteStream(3, CancellationToken.None, RecordSegments(allocated));
        output.Write([1, 2, 3]);

        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => output.WriteByte(4));

        Assert.Equal(SvgStaticSnapshotResourceException.OutputBytesReason, exception.Reason);
        Assert.True(output.LimitExceeded);
        Assert.Equal(3L, output.Length);
        Assert.Single(allocated);
        output.Dispose();
        AssertSegmentsClearedAndReleased(output, allocated);
    }

    [Fact]
    public void SegmentedOutputCancellationStopsWritesAndClearsBuffers()
    {
        using var cancellation = new CancellationTokenSource();
        var allocated = new List<byte[]>();
        var output = new BoundedSegmentedWriteStream(32, cancellation.Token, RecordSegments(allocated));
        output.Write([1, 2, 3]);

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => output.WriteByte(4));

        output.Dispose();
        AssertSegmentsClearedAndReleased(output, allocated);
    }

    [Fact]
    public void SegmentedOutputCancellationDuringFinalizationDoesNotReturnPartialBytes()
    {
        using var cancellation = new CancellationTokenSource();
        var allocated = new List<byte[]>();
        var output = new BoundedSegmentedWriteStream(32, cancellation.Token, RecordSegments(allocated));
        output.Write([1, 2, 3]);

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => output.ToArray());

        output.Dispose();
        AssertSegmentsClearedAndReleased(output, allocated);
    }

    [Fact]
    public void SegmentedOutputCannotBeConfiguredAboveHardSourceCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedSegmentedWriteStream(0, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SvgStaticSnapshot.Create([], CancellationToken.None, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedSegmentedWriteStream(
                SvgResourceBudget.MaxInputBytes + 1,
                CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SvgStaticSnapshot.Create([], CancellationToken.None, SvgResourceBudget.MaxInputBytes + 1));
    }

    private static Func<int, byte[]> RecordSegments(List<byte[]> allocated) => size =>
    {
        byte[] segment = new byte[size];
        allocated.Add(segment);
        return segment;
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertSegmentsClearedAndReleased(BoundedSegmentedWriteStream output, List<byte[]> allocated)
    {
        Assert.NotEmpty(allocated);
        Assert.All(allocated, static segment => Assert.All(segment, static value => Assert.Equal(0, value)));
        WeakReference[] released = ToWeakReferences(allocated);
        allocated.Clear();
        ForceCollection();
        Assert.All(released, static segment => Assert.False(segment.IsAlive));
        GC.KeepAlive(output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ToWeakReferences(List<byte[]> values) =>
        values.Select(static value => new WeakReference(value)).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
