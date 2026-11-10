namespace MarkdownRenderer.Performance.Internal.CompletedScenes;

/// <summary>Saturating arithmetic shared by the completed-scene weight estimators.</summary>
internal static class CompletedSceneWeightMath
{
    internal static long Multiply(long left, long right) =>
        left <= 0 || right <= 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    internal static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
