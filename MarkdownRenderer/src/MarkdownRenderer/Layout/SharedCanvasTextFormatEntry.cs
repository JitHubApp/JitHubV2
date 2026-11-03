using System;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Reference-counted owner of one cached CanvasTextFormat. The final release
/// disposes the format through its preallocated retirement record.
/// </summary>
internal sealed class SharedCanvasTextFormatEntry
{
    private readonly NativeResourceRetirementRecord _retirementRecord;
    private int _referenceCount = 1; // caller/acquisition reference

    internal SharedCanvasTextFormatEntry(CanvasTextFormat format, long weightBytes, NativeResourceRetirementRecord retirementRecord)
    {
        Format = format;
        WeightBytes = Math.Max(1, weightBytes);
        _retirementRecord = retirementRecord;
    }

    internal readonly CanvasTextFormat Format;
    internal readonly long WeightBytes;

    /// <summary>Live acquisition plus cache references; zero once retired.</summary>
    internal int ReferenceCount => Volatile.Read(ref _referenceCount);

    internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

    internal bool TryAcquire()
    {
        int observed = Volatile.Read(ref _referenceCount);
        while (observed > 0)
        {
            if (observed == int.MaxValue)
                return false;
            int updated = Interlocked.CompareExchange(ref _referenceCount, observed + 1, observed);
            if (updated == observed)
                return true;
            observed = updated;
        }
        return false;
    }

    internal void ReleaseCacheReference() => Release();

    internal Exception? Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0)
            return null;

        try
        {
            _retirementRecord.Disposer(Format);
            _retirementRecord.SetTemporary(0, null);
            _retirementRecord.Publish();
            return null;
        }
        catch (Exception cleanupFailure)
        {
            // A final release cannot retain Entry or its immutable fields.
            // Its preallocated source-free record already contains Format.
            _retirementRecord.Publish();
            return cleanupFailure;
        }
    }
}
