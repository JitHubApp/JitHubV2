using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Captures the measured state of one atomic inline run before a
/// speculative inline-container measure, so a failed measure can restore
/// the run exactly as it was.
/// </summary>
internal readonly struct InlineMeasureRollbackState
{
    private readonly InlineImageRun? _imageRun;
    private readonly InlineVectorSceneRun? _vectorRun;
    private readonly float _desiredWidth;
    private readonly float _desiredHeight;
    private readonly float _desiredBaseline;
    private readonly Rect _localBounds;
    private readonly InlineMeasureCheckpoint _imageCheckpoint;

    internal InlineMeasureRollbackState(
        InlineImageRun? imageRun,
        InlineVectorSceneRun? vectorRun,
        float desiredWidth,
        float desiredHeight,
        float desiredBaseline,
        Rect localBounds,
        InlineMeasureCheckpoint imageCheckpoint = default)
    {
        _imageRun = imageRun;
        _vectorRun = vectorRun;
        _desiredWidth = desiredWidth;
        _desiredHeight = desiredHeight;
        _desiredBaseline = desiredBaseline;
        _localBounds = localBounds;
        _imageCheckpoint = imageCheckpoint;
    }

    internal void Commit() => _imageCheckpoint.Commit();

    internal void Restore()
    {
        _imageCheckpoint.Rollback();
        if (_imageRun is { } image)
            image.RestoreMeasuredSize(_desiredWidth, _desiredHeight);
        if (_vectorRun is { } vector)
        {
            vector.RestoreLayoutState(
                _desiredWidth,
                _desiredHeight,
                _desiredBaseline,
                _localBounds);
        }
    }
}
