using System;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Owns an image box's measured geometry and the placeholder and caption text
/// layouts measured with it, under one lock. Every layout is paired with a
/// retirement record reserved before Win2D allocates it, and a speculative
/// inline measure keeps the replaced pairs alive until it commits or rolls back.
/// </summary>
internal sealed class ImageMeasureCustody
{
    private readonly IImageMeasureHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly NativeResourceRetirement _nativeRetirement;
    private readonly Action<IDisposable>? _nativeDisposer;
    private readonly Action<CanvasTextLayout>? _nativeLayoutOptionsSetter;

    internal ImageMeasureCustody(
        IImageMeasureHost host,
        MarkdownLayoutContext context,
        NativeResourceRetirement nativeRetirement,
        Action<IDisposable>? nativeDisposer,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter)
    {
        _host = host;
        _context = context;
        _nativeRetirement = nativeRetirement;
        _nativeDisposer = nativeDisposer;
        _nativeLayoutOptionsSetter = nativeLayoutOptionsSetter;
    }

    /// <summary>Guards the geometry, the layouts, and the checkpoint below.</summary>
    internal readonly object Sync = new();
    internal bool IsInlineLayout;
    internal float AvailableWidth;
    internal float ImageWidth;
    internal float ImageHeight;
    internal float CaptionHeight;
    internal CanvasTextLayout? Placeholder;
    internal CanvasTextLayout? Caption;
    internal NativeResourceRetirementRecord? PlaceholderRecord;
    internal NativeResourceRetirementRecord? CaptionRecord;
    internal bool CheckpointActive;
    internal int CheckpointGeneration;
    internal CanvasTextLayout? OriginalPlaceholder;
    internal NativeResourceRetirementRecord? OriginalPlaceholderRecord;
    internal CanvasTextLayout? OriginalCaption;
    internal NativeResourceRetirementRecord? OriginalCaptionRecord;
    internal int NativeLayoutGeneration;

    /// <summary>A custody for a clone, sharing this one's retirement owner and fault plan.</summary>
    internal ImageMeasureCustody CreateForClone(IImageMeasureHost host, MarkdownLayoutContext context) =>
        new(host, context, _nativeRetirement, _nativeDisposer, _nativeLayoutOptionsSetter);

    /// <summary>One consistent read of the layouts and their retirement records.</summary>
    internal ImageTextLayoutState GetLayoutState()
    {
        lock (Sync)
        {
            return new ImageTextLayoutState(
                Placeholder, PlaceholderRecord, Caption, CaptionRecord,
                HasValidNativeLayoutCustodyNoLock());
        }
    }

    /// <summary>
    /// Replaces the caption's retirement record without touching its layout,
    /// so a caller can stage an aliased or foreign owner.
    /// </summary>
    internal void ReplaceCaptionRecord(NativeResourceRetirementRecord? record)
    {
        lock (Sync)
        {
            CaptionRecord = record;
            AdvanceNativeLayoutGenerationNoLock();
        }
    }

    /// <summary>
    /// Opens a speculative inline-measure checkpoint over the current layouts
    /// and returns its generation. The caller holds <see cref="Sync"/> and has
    /// checked that the box is live.
    /// </summary>
    internal int OpenCheckpointNoLock()
    {
        if (CheckpointActive)
            throw new InvalidOperationException("An inline image measure is already active.");

        int generation = unchecked(CheckpointGeneration + 1);
        CheckpointGeneration = generation == 0 ? 1 : generation;
        CheckpointActive = true;
        OriginalPlaceholder = Placeholder;
        OriginalPlaceholderRecord = PlaceholderRecord;
        OriginalCaption = Caption;
        OriginalCaptionRecord = CaptionRecord;
        AdvanceNativeLayoutGenerationNoLock();
        return CheckpointGeneration;
    }

    /// <summary>
    /// Commits or rolls back the open checkpoint. The caller holds
    /// <see cref="Sync"/> and has checked that the checkpoint is current.
    /// </summary>
    internal void FinishCheckpointNoLock(
        ImageBox.InlineMeasureCheckpoint checkpoint,
        bool commit)
    {
        CanvasTextLayout? originalPlaceholder = OriginalPlaceholder;
        NativeResourceRetirementRecord? originalPlaceholderRecord = OriginalPlaceholderRecord;
        CanvasTextLayout? originalCaption = OriginalCaption;
        NativeResourceRetirementRecord? originalCaptionRecord = OriginalCaptionRecord;
        if (_host.IsDisposed)
        {
            // Dispose owns both current and checkpoint-original pairs.
            // Revoke this token but leave every layout/record attached
            // until the asynchronous terminal cleanup can attempt them.
            RevokeInlineCheckpointNoLock();
            return;
        }

        if (commit)
        {
            ClearInlineCheckpoint();
            RetireIfDetached(originalPlaceholder, originalPlaceholderRecord,
                Placeholder, PlaceholderRecord);
            RetireIfDetached(originalCaption, originalCaptionRecord,
                Caption, CaptionRecord);
            return;
        }

        CanvasTextLayout? candidatePlaceholder = Placeholder;
        NativeResourceRetirementRecord? candidatePlaceholderRecord = PlaceholderRecord;
        CanvasTextLayout? candidateCaption = Caption;
        NativeResourceRetirementRecord? candidateCaptionRecord = CaptionRecord;
        if (!HasValidNativeLayoutCustodyNoLock() ||
            (originalPlaceholder is null) != (originalPlaceholderRecord is null) ||
            (originalCaption is null) != (originalCaptionRecord is null) ||
            (candidatePlaceholder is null) != (candidatePlaceholderRecord is null) ||
            (candidateCaption is null) != (candidateCaptionRecord is null))
        {
            throw new InvalidOperationException("Image inline-checkpoint native-layout custody is inconsistent.");
        }

        Placeholder = originalPlaceholder;
        PlaceholderRecord = originalPlaceholderRecord;
        Caption = originalCaption;
        CaptionRecord = originalCaptionRecord;
        IsInlineLayout = checkpoint.WasInlineLayout;
        AvailableWidth = checkpoint.PreviousAvailableWidth;
        ImageWidth = checkpoint.PreviousImageWidth;
        ImageHeight = checkpoint.PreviousImageHeight;
        CaptionHeight = checkpoint.PreviousCaptionHeight;
        _host.RestoreBounds(checkpoint.PreviousBounds);
        AdvanceNativeLayoutGenerationNoLock();
        ClearInlineCheckpoint();

        // Restore the exact original owner pair before publishing the
        // detached candidates. The records are pre-reserved and this
        // handoff invokes no native callbacks under the image lock.
        RetireIfDetached(candidatePlaceholder, candidatePlaceholderRecord,
            Placeholder, PlaceholderRecord);
        RetireIfDetached(candidateCaption, candidateCaptionRecord,
            Caption, CaptionRecord);
    }

    private void ClearInlineCheckpoint()
    {
        CheckpointActive = false;
        OriginalPlaceholder = null;
        OriginalPlaceholderRecord = null;
        OriginalCaption = null;
        OriginalCaptionRecord = null;
    }

    internal void InvalidateInlineCheckpointForDisposal()
    {
        lock (Sync)
        {
            if (!CheckpointActive)
                return;

            // Do not publish the original owners here: RetirementTask still
            // has to make its synchronous best-effort disposal attempt and
            // surface those failures. Late checkpoint completion is revoked
            // while its exact original pairs remain attached for that drain.
            RevokeInlineCheckpointNoLock();
        }
    }

    private void RevokeInlineCheckpointNoLock()
    {
        CheckpointActive = false;
        int generation = unchecked(CheckpointGeneration + 1);
        CheckpointGeneration = generation == 0 ? 1 : generation;
        AdvanceNativeLayoutGenerationNoLock();
    }

    private static void RetireNativeLayout(
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

    private static void RetireIfDetached(
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

    internal NativeResourceRetirementRecord ReserveNativeLayoutRetirementRecord() =>
        _nativeRetirement.Reserve(0, 1, _nativeDisposer);

    internal CanvasTextLayout CreateOwnedTextLayout(
        string text,
        CanvasTextFormat format,
        float width,
        float height,
        out NativeResourceRetirementRecord retirementRecord)
    {
        // Reserve the source-free owner before Win2D allocates its layout.
        NativeResourceRetirementRecord reserved = ReserveNativeLayoutRetirementRecord();
        CanvasTextLayout? layout = null;
        try
        {
            layout = CanvasTextLayoutFactory.Create(
                _context.ResourceCreator,
                text,
                format,
                width,
                height,
                _context.LayoutDiagnostics);
            reserved.SetTemporary(0, layout);
            if (_nativeLayoutOptionsSetter is { } testSetter)
                testSetter(layout);
            else
                layout.Options = CanvasDrawTextOptions.EnableColorFont;
            retirementRecord = reserved;
            return layout;
        }
        catch
        {
            if (layout is not null)
                reserved.SetTemporary(0, layout);
            reserved.Publish();
            throw;
        }
    }

    internal void AdvanceNativeLayoutGenerationNoLock() =>
        NativeLayoutGeneration = unchecked(NativeLayoutGeneration + 1);

    internal bool HasValidNativeLayoutCustodyNoLock() =>
        (Placeholder is null) == (PlaceholderRecord is null) &&
        (Caption is null) == (CaptionRecord is null) &&
        (Placeholder is null || Caption is null || !ReferenceEquals(Placeholder, Caption)) &&
        (PlaceholderRecord is null || CaptionRecord is null ||
            !ReferenceEquals(PlaceholderRecord, CaptionRecord));

    internal void ReplacePlaceholder(
        CanvasTextLayout? replacement,
        NativeResourceRetirementRecord? replacementRecord)
    {
        CanvasTextLayout? previous;
        NativeResourceRetirementRecord? previousRecord;
        bool checkpointOwnsPrevious;
        try
        {
            lock (Sync)
            {
                if (_host.IsDisposed && replacement is not null)
                    throw new ObjectDisposedException(nameof(ImageBox));

                if (!HasValidNativeLayoutCustodyNoLock() ||
                    (replacement is null) != (replacementRecord is null))
                {
                    throw new InvalidOperationException("Image placeholder native-layout custody is inconsistent.");
                }

                previous = Placeholder;
                previousRecord = PlaceholderRecord;
                checkpointOwnsPrevious = CheckpointActive &&
                    ReferenceEquals(previous, OriginalPlaceholder) &&
                    ReferenceEquals(previousRecord, OriginalPlaceholderRecord);
                Placeholder = replacement;
                PlaceholderRecord = replacementRecord;
                AdvanceNativeLayoutGenerationNoLock();
            }
        }
        catch
        {
            RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            RetireNativeLayout(previous, previousRecord);
    }

    internal void ReplaceCaption(
        CanvasTextLayout? replacement,
        NativeResourceRetirementRecord? replacementRecord,
        float replacementHeight)
    {
        CanvasTextLayout? previous;
        NativeResourceRetirementRecord? previousRecord;
        bool checkpointOwnsPrevious;
        try
        {
            lock (Sync)
            {
                if (_host.IsDisposed && replacement is not null)
                    throw new ObjectDisposedException(nameof(ImageBox));

                if (!HasValidNativeLayoutCustodyNoLock() ||
                    (replacement is null) != (replacementRecord is null))
                {
                    throw new InvalidOperationException("Image caption native-layout custody is inconsistent.");
                }

                previous = Caption;
                previousRecord = CaptionRecord;
                checkpointOwnsPrevious = CheckpointActive &&
                    ReferenceEquals(previous, OriginalCaption) &&
                    ReferenceEquals(previousRecord, OriginalCaptionRecord);
                Caption = replacement;
                CaptionRecord = replacementRecord;
                CaptionHeight = replacementHeight;
                AdvanceNativeLayoutGenerationNoLock();
            }
        }
        catch
        {
            RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            RetireNativeLayout(previous, previousRecord);
    }

    internal void RetireNativeLayoutsForDisposal(Action<Exception> addFailure)
    {
        CanvasTextLayout? placeholder;
        CanvasTextLayout? caption;
        CanvasTextLayout? originalPlaceholder;
        CanvasTextLayout? originalCaption;
        NativeResourceRetirementRecord? placeholderRecord;
        NativeResourceRetirementRecord? captionRecord;
        NativeResourceRetirementRecord? originalPlaceholderRecord;
        NativeResourceRetirementRecord? originalCaptionRecord;
        lock (Sync)
        {
            placeholder = Placeholder;
            caption = Caption;
            originalPlaceholder = OriginalPlaceholder;
            originalCaption = OriginalCaption;
            placeholderRecord = PlaceholderRecord;
            captionRecord = CaptionRecord;
            originalPlaceholderRecord = OriginalPlaceholderRecord;
            originalCaptionRecord = OriginalCaptionRecord;

            if (!HasValidNativeLayoutCustodyNoLock() ||
                (originalPlaceholder is null) != (originalPlaceholderRecord is null) ||
                (originalCaption is null) != (originalCaptionRecord is null) ||
                !HasCoherentTerminalOwnerPairs(
                    placeholder, placeholderRecord,
                    caption, captionRecord,
                    originalPlaceholder, originalPlaceholderRecord,
                    originalCaption, originalCaptionRecord))
            {
                throw new InvalidOperationException("Image terminal native-layout custody is inconsistent.");
            }

            Placeholder = null;
            PlaceholderRecord = null;
            Caption = null;
            CaptionRecord = null;
            CheckpointActive = false;
            OriginalPlaceholder = null;
            OriginalPlaceholderRecord = null;
            OriginalCaption = null;
            OriginalCaptionRecord = null;
            AdvanceNativeLayoutGenerationNoLock();
        }

        NativeRetirementFailurePair placeholderFailure =
            RetireTerminalNativeLayout(placeholder, placeholderRecord);
        NativeRetirementFailurePair captionFailure = default;
        NativeRetirementFailurePair originalPlaceholderFailure = default;
        NativeRetirementFailurePair originalCaptionFailure = default;
        if (!ReferenceEquals(caption, placeholder) || !ReferenceEquals(captionRecord, placeholderRecord))
            captionFailure = RetireTerminalNativeLayout(caption, captionRecord);
        if (!ReferenceEquals(originalPlaceholder, placeholder) ||
            !ReferenceEquals(originalPlaceholderRecord, placeholderRecord))
            originalPlaceholderFailure = RetireTerminalNativeLayout(
                originalPlaceholder, originalPlaceholderRecord);
        if ((!ReferenceEquals(originalCaption, placeholder) ||
             !ReferenceEquals(originalCaptionRecord, placeholderRecord)) &&
            (!ReferenceEquals(originalCaption, caption) ||
             !ReferenceEquals(originalCaptionRecord, captionRecord)) &&
            (!ReferenceEquals(originalCaption, originalPlaceholder) ||
             !ReferenceEquals(originalCaptionRecord, originalPlaceholderRecord)))
        {
            originalCaptionFailure = RetireTerminalNativeLayout(originalCaption, originalCaptionRecord);
        }

        // Every detached record has either completed or reached the durable
        // retry registry before diagnostics can allocate, aggregate, or fail.
        AddRetirementFailure(addFailure, placeholderFailure);
        AddRetirementFailure(addFailure, captionFailure);
        AddRetirementFailure(addFailure, originalPlaceholderFailure);
        AddRetirementFailure(addFailure, originalCaptionFailure);
    }

    private static bool HasCoherentTerminalOwnerPairs(
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

    private static NativeRetirementFailurePair RetireTerminalNativeLayout(
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

    private static void AddRetirementFailure(
        Action<Exception> addFailure,
        NativeRetirementFailurePair failures)
    {
        if (failures.DisposalFailure is { } disposalFailure)
            addFailure(disposalFailure);
        if (failures.PublicationFailure is { } publicationFailure)
            addFailure(publicationFailure);
    }
}
