using System;

namespace MarkdownRenderer.Controls;

internal static class LazyLayoutScrollAnchorPolicy
{
    internal static bool ShouldRestore(
        double? capturedOffset,
        double? currentOffset,
        bool scrollInProgress) =>
        !scrollInProgress &&
        capturedOffset is { } captured &&
        currentOffset is { } current &&
        double.IsFinite(captured) &&
        double.IsFinite(current) &&
        Math.Abs(current - captured) < 0.5;
}
