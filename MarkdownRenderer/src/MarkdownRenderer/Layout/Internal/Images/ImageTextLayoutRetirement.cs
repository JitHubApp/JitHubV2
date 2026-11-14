using System;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Internal.RenderResources;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Retires the image text layouts an <see cref="ImageMeasureCustody"/> has
/// detached: publishes each layout's pre-reserved retirement record, checks
/// that a terminal set of current and checkpoint-original owner pairs is
/// coherent, and makes the terminal best-effort close outside the image lock.
/// </summary>
internal static class ImageTextLayoutRetirement
{
    internal static void RetireNativeLayout(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord)
    {
        if (retirementRecord is null)
        {
            if (layout is not null)
                throw new InvalidOperationException("A detached image text layout has no durable retirement owner.");
            return;
        }

        // Every participating layout is paired with a record reserved before
        // construction. Never invoke native disposal on a detached layout or
        // lose it if Win2D disposal fails.
        retirementRecord?.Publish();
    }

    internal static void RetireIfDetached(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        CanvasTextLayout? activeLayout,
        NativeResourceRetirementRecord? activeRecord)
    {
        if (layout is null && retirementRecord is null)
            return;
        if (ReferenceEquals(layout, activeLayout) && ReferenceEquals(retirementRecord, activeRecord))
            return;
        RetireNativeLayout(layout, retirementRecord);
    }

    internal static bool HasCoherentTerminalOwnerPairs(
        CanvasTextLayout? placeholder,
        NativeResourceRetirementRecord? placeholderRecord,
        CanvasTextLayout? caption,
        NativeResourceRetirementRecord? captionRecord,
        CanvasTextLayout? originalPlaceholder,
        NativeResourceRetirementRecord? originalPlaceholderRecord,
        CanvasTextLayout? originalCaption,
        NativeResourceRetirementRecord? originalCaptionRecord)
    {
        return SameOrSeparateOwner(placeholder, placeholderRecord,
                   originalPlaceholder, originalPlaceholderRecord) &&
               SameOrSeparateOwner(caption, captionRecord,
                   originalCaption, originalCaptionRecord) &&
               AreDisjointOwners(placeholder, placeholderRecord, caption, captionRecord) &&
               AreDisjointOwners(placeholder, placeholderRecord, originalCaption, originalCaptionRecord) &&
               AreDisjointOwners(caption, captionRecord, originalPlaceholder, originalPlaceholderRecord) &&
               AreDisjointOwners(originalPlaceholder, originalPlaceholderRecord,
                   originalCaption, originalCaptionRecord);
    }

    private static bool SameOrSeparateOwner(
        CanvasTextLayout? leftLayout,
        NativeResourceRetirementRecord? leftRecord,
        CanvasTextLayout? rightLayout,
        NativeResourceRetirementRecord? rightRecord) =>
        ReferenceEquals(leftLayout, rightLayout) == ReferenceEquals(leftRecord, rightRecord);

    private static bool AreDisjointOwners(
        CanvasTextLayout? leftLayout,
        NativeResourceRetirementRecord? leftRecord,
        CanvasTextLayout? rightLayout,
        NativeResourceRetirementRecord? rightRecord) =>
        (leftLayout is null || rightLayout is null || !ReferenceEquals(leftLayout, rightLayout)) &&
        (leftRecord is null || rightRecord is null || !ReferenceEquals(leftRecord, rightRecord));

    internal static NativeRetirementFailurePair RetireTerminalNativeLayout(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? record)
    {
        if (layout is null)
            return default;
        if (record is null)
        {
            return new NativeRetirementFailurePair(
                new InvalidOperationException("A terminal image text layout has no pre-reserved native owner."),
                null);
        }

        try
        {
            // Preserve the existing RetirementTask contract: make one
            // synchronous best-effort close outside the image lock. A failure
            // is published before its bounded diagnostic is recorded.
            record.Disposer(layout);
            record.SetTemporary(0, null);
            record.Publish();
        }
        catch (Exception failure)
        {
            try
            {
                record.Publish();
                return new NativeRetirementFailurePair(failure, null);
            }
            catch (Exception publicationFailure)
            {
                return new NativeRetirementFailurePair(failure, publicationFailure);
            }
        }

        return default;
    }

    internal static void AddRetirementFailure(
        Action<Exception> addFailure,
        NativeRetirementFailurePair failures)
    {
        if (failures.DisposalFailure is { } disposalFailure)
            addFailure(disposalFailure);
        if (failures.PublicationFailure is { } publicationFailure)
            addFailure(publicationFailure);
    }
}
