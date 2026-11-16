using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapCache;

/// <summary>Numeric-only accounting; never retains a resource reference.</summary>
internal sealed class LiveBitmapPixelWeightLedger
{
    private long _livePixelWeightBytes;
    private long _uncertainDisposePixelWeightBytes;
    internal long LivePixelWeightBytes => Volatile.Read(ref _livePixelWeightBytes);
    internal long UncertainDisposePixelWeightBytes => Volatile.Read(ref _uncertainDisposePixelWeightBytes);

    internal void Register(long pixelWeightBytes)
    {
        if (pixelWeightBytes > 0)
            Interlocked.Add(ref _livePixelWeightBytes, pixelWeightBytes);
    }

    internal void AbandonRegistration(long pixelWeightBytes)
    {
        if (pixelWeightBytes > 0)
            Interlocked.Add(ref _livePixelWeightBytes, -pixelWeightBytes);
    }

    internal void ReleaseSuccessfulDispose(long pixelWeightBytes)
    {
        if (pixelWeightBytes > 0)
            Interlocked.Add(ref _livePixelWeightBytes, -pixelWeightBytes);
    }

    internal void RecordUncertainDispose(long pixelWeightBytes)
    {
        if (pixelWeightBytes > 0)
            Interlocked.Add(ref _uncertainDisposePixelWeightBytes, pixelWeightBytes);
    }

    internal void ReleaseFinalReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes)
    {
        if (Interlocked.Decrement(ref referenceCount) != 0)
            return;
        try { resource.Dispose(); }
        catch
        {
            // Native disposal state is unknown: retain its conservative
            // charge without retaining the failed native object.
            RecordUncertainDispose(pixelWeightBytes);
            throw;
        }
        ReleaseSuccessfulDispose(pixelWeightBytes);
    }
}
