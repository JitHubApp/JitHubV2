using System;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Retires inline text layouts through their pre-reserved native-retirement
/// records and aggregates cleanup failures without masking the primary
/// exception.
/// </summary>
internal static class InlineNativeLayoutRetirement
{
    internal static Exception? RetireNativeLayout(
        ref CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        string operation)
    {
        if (layout is null)
            return ReleaseEmptyNativeRecord(retirementRecord);

        // Missing custody is a structural error, not permission to attempt a
        // potentially throwing Dispose and lose the last handle. Participating
        // creation paths reserve the record before the native factory.
        if (retirementRecord is null)
            return new InvalidOperationException(
                $"Cannot retire a native inline layout during {operation} without its pre-reserved owner.");

        CanvasTextLayout current = layout;
        Exception? cleanupFailure = null;
        try
        {
            retirementRecord.Disposer(current);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        // Custody handoff is deliberately allocation-free between the native
        // failure and Publish. On failure the existing slot keeps the layout;
        // on success it is cleared. The caller-visible ref is cleared only
        // after a failed resource is rooted in its record.
        retirementRecord.SetTemporary(0, cleanupFailure is null ? null : current);
        layout = null;
        retirementRecord.Publish();
        return cleanupFailure;
    }

    internal static Exception? ReleaseEmptyNativeRecord(
        NativeResourceRetirementRecord? retirementRecord)
    {
        if (retirementRecord is null)
            return null;
        try
        {
            retirementRecord.SetTemporary(0, null);
            retirementRecord.Publish();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static Exception? CombineFailures(
        string operation,
        Exception? first,
        Exception? second)
    {
        if (first is null)
            return second;
        if (second is null)
            return first;
        return new AggregateException($"One or more errors occurred while {operation}.", first, second);
    }
}
