using System.Diagnostics.Tracing;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Counts raster preparation decode and upload events and records the highest
/// bitmap output charge observed at each of them, so a storm test can prove
/// that the shared ledger never exceeded its capacity.
/// </summary>
internal sealed class RasterPreparationChargeTrace(BitmapOutputAdmission admission) : EventListener
{
    private long _maximumChargedBytes;
    private int _wicPixelDecodeCount;
    private int _win2DBitmapUploadCount;
    private int _capacityViolationCount;

    internal long MaximumChargedBytes => Interlocked.Read(ref _maximumChargedBytes);
    internal int WicPixelDecodeCount => Volatile.Read(ref _wicPixelDecodeCount);
    internal int Win2DBitmapUploadCount => Volatile.Read(ref _win2DBitmapUploadCount);
    internal int CapacityViolationCount => Volatile.Read(ref _capacityViolationCount);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "MarkdownRenderer-RasterPreparation")
            EnableEvents(eventSource, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name != "MarkdownRenderer-RasterPreparation" ||
            eventData.EventId != 2 ||
            eventData.Payload is not { Count: 3 } payload ||
            payload[1] is not long stage)
        {
            return;
        }

        if (stage == (long)MarkdownRasterPreparationStage.WicPixelDecode)
            Interlocked.Increment(ref _wicPixelDecodeCount);
        else if (stage == (long)MarkdownRasterPreparationStage.Win2DBitmapUpload)
            Interlocked.Increment(ref _win2DBitmapUploadCount);
        else if (stage != (long)MarkdownRasterPreparationStage.Win2DDirectLoad &&
                 stage != (long)MarkdownRasterPreparationStage.CachePublication)
            return;

        BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
        long observed;
        while (snapshot.ChargedBytes > (observed = Interlocked.Read(ref _maximumChargedBytes)) &&
               Interlocked.CompareExchange(
                   ref _maximumChargedBytes,
                   snapshot.ChargedBytes,
                   observed) != observed)
        {
        }

        if (snapshot.ChargedBytes > snapshot.CapacityBytes)
            Interlocked.Increment(ref _capacityViolationCount);
    }
}
