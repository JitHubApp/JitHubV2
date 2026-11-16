using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Images;

namespace MarkdownRenderer.Layout.Internal.Inlines.SpeculativeMeasurement;

/// <summary>
/// Value checkpoint for one speculative inline measurement. The image box
/// keeps replaced native layouts alive until this checkpoint is committed
/// or rolled back.
/// </summary>
internal readonly struct InlineMeasureCheckpoint
{
    internal readonly ImageBox? Owner;
    internal readonly int Generation;
    internal readonly bool WasInlineLayout;
    internal readonly float PreviousAvailableWidth;
    internal readonly float PreviousImageWidth;
    internal readonly float PreviousImageHeight;
    internal readonly float PreviousCaptionHeight;
    internal readonly Rect PreviousBounds;

    internal InlineMeasureCheckpoint(ImageBox owner, int generation)
    {
        Owner = owner;
        Generation = generation;
        WasInlineLayout = owner.MeasureCustody.IsInlineLayout;
        PreviousAvailableWidth = owner.MeasureCustody.AvailableWidth;
        PreviousImageWidth = owner.MeasureCustody.ImageWidth;
        PreviousImageHeight = owner.MeasureCustody.ImageHeight;
        PreviousCaptionHeight = owner.MeasureCustody.CaptionHeight;
        PreviousBounds = owner.Bounds;
    }

    internal bool BelongsTo(ImageBox owner, int generation) =>
        ReferenceEquals(Owner, owner) && Generation == generation;

    internal void Commit() => Owner?.FinishInlineCheckpoint(this, commit: true);

    internal void Rollback() => Owner?.FinishInlineCheckpoint(this, commit: false);
}
