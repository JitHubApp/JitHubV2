using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Layout.Internal.Inlines.SpeculativeMeasurement;

/// <summary>
/// The local bounds an inline vector scene run had before a speculative
/// measure, so a failed measure can put the run back.
/// </summary>
internal readonly struct InlineScenePlacementRollbackState
{
    internal InlineScenePlacementRollbackState(InlineVectorSceneRun run, Rect localBounds)
    {
        Run = run;
        LocalBounds = localBounds;
    }

    internal readonly InlineVectorSceneRun Run;
    internal readonly Rect LocalBounds;
}
