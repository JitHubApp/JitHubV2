using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

internal static class RasterNativeWicDiagnostics
{
    private static long s_nativeStarted;
    private static long s_pixelLeaseCompleted;

    internal static RasterNativeWicDiagnosticSnapshot GetSnapshot() =>
        new(Interlocked.Read(ref s_nativeStarted), Interlocked.Read(ref s_pixelLeaseCompleted));

    internal static void RecordNativeStarted() => Interlocked.Increment(ref s_nativeStarted);
    internal static void RecordPixelLeaseCompleted() => Interlocked.Increment(ref s_pixelLeaseCompleted);
}
