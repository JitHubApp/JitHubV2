using System;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines.SpeculativeMeasurement;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.Layout.Internal.Images;

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
    private readonly Action<CanvasTextLayout>? _nativeLayoutOptionsSetter;

    internal ImageMeasureCustody(
        IImageMeasureHost host,
        MarkdownLayoutContext context,
        NativeResourceRetirement nativeRetirement,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter)
    {
        _host = host;
        _context = context;
        _nativeRetirement = nativeRetirement;
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

    /// <summary>A custody for a clone, sharing this one's retirement owner and layout-options hook.</summary>
    internal ImageMeasureCustody CreateForClone(IImageMeasureHost host, MarkdownLayoutContext context) =>
        new(host, context, _nativeRetirement, _nativeLayoutOptionsSetter);

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
        InlineMeasureCheckpoint checkpoint,
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
            ImageTextLayoutRetirement.RetireIfDetached(originalPlaceholder, originalPlaceholderRecord,
                Placeholder, PlaceholderRecord);
            ImageTextLayoutRetirement.RetireIfDetached(originalCaption, originalCaptionRecord,
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
        ImageTextLayoutRetirement.RetireIfDetached(candidatePlaceholder, candidatePlaceholderRecord,
            Placeholder, PlaceholderRecord);
        ImageTextLayoutRetirement.RetireIfDetached(candidateCaption, candidateCaptionRecord,
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

    internal NativeResourceRetirementRecord ReserveNativeLayoutRetirementRecord() =>
        _nativeRetirement.Reserve(0, 1);

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
            if (_nativeLayoutOptionsSetter is { } optionsSetter)
                optionsSetter(layout);
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
            ImageTextLayoutRetirement.RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            ImageTextLayoutRetirement.RetireNativeLayout(previous, previousRecord);
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
            ImageTextLayoutRetirement.RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            ImageTextLayoutRetirement.RetireNativeLayout(previous, previousRecord);
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
                !ImageTextLayoutRetirement.HasCoherentTerminalOwnerPairs(
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
            ImageTextLayoutRetirement.RetireTerminalNativeLayout(placeholder, placeholderRecord);
        NativeRetirementFailurePair captionFailure = default;
        NativeRetirementFailurePair originalPlaceholderFailure = default;
        NativeRetirementFailurePair originalCaptionFailure = default;
        if (!ReferenceEquals(caption, placeholder) || !ReferenceEquals(captionRecord, placeholderRecord))
            captionFailure = ImageTextLayoutRetirement.RetireTerminalNativeLayout(caption, captionRecord);
        if (!ReferenceEquals(originalPlaceholder, placeholder) ||
            !ReferenceEquals(originalPlaceholderRecord, placeholderRecord))
            originalPlaceholderFailure = ImageTextLayoutRetirement.RetireTerminalNativeLayout(
                originalPlaceholder, originalPlaceholderRecord);
        if ((!ReferenceEquals(originalCaption, placeholder) ||
             !ReferenceEquals(originalCaptionRecord, placeholderRecord)) &&
            (!ReferenceEquals(originalCaption, caption) ||
             !ReferenceEquals(originalCaptionRecord, captionRecord)) &&
            (!ReferenceEquals(originalCaption, originalPlaceholder) ||
             !ReferenceEquals(originalCaptionRecord, originalPlaceholderRecord)))
        {
            originalCaptionFailure = ImageTextLayoutRetirement.RetireTerminalNativeLayout(originalCaption, originalCaptionRecord);
        }

        // Every detached record has either completed or reached the durable
        // retry registry before diagnostics can allocate, aggregate, or fail.
        ImageTextLayoutRetirement.AddRetirementFailure(addFailure, placeholderFailure);
        ImageTextLayoutRetirement.AddRetirementFailure(addFailure, captionFailure);
        ImageTextLayoutRetirement.AddRetirementFailure(addFailure, originalPlaceholderFailure);
        ImageTextLayoutRetirement.AddRetirementFailure(addFailure, originalCaptionFailure);
    }
}
