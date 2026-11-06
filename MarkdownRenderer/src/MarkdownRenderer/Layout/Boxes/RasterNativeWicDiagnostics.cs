using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Counts native WIC decodes for one image resource scope: how many reached
/// the native graph and how many returned a completed pixel lease.
/// </summary>
internal sealed class RasterNativeWicDiagnostics
{
    private long _nativeStarted;
    private long _pixelLeaseCompleted;

    internal RasterNativeWicDiagnosticSnapshot GetSnapshot() =>
        new(Interlocked.Read(ref _nativeStarted), Interlocked.Read(ref _pixelLeaseCompleted));

    internal void RecordNativeStarted() => Interlocked.Increment(ref _nativeStarted);
    internal void RecordPixelLeaseCompleted() => Interlocked.Increment(ref _pixelLeaseCompleted);
}
