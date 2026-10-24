internal sealed record SourceReplayTerminalClampObservation
{
    public int ViewportIndex { get; init; }
    public int MovementIndex { get; init; }
    public double RequestedOffsetViewportUnits { get; init; }
    public double ActualOffsetViewportUnits { get; init; }
    public double MaximumOffsetViewportUnits { get; init; }
    public int DocumentHeightPx { get; init; }
    public int ViewportHeightPx { get; init; }
}

internal sealed record SourceReplayReadmeIdentity(
    string ReadmeGitBlobSha1,
    string ReadmeSha256,
    long ByteSize,
    string DecodedTextSha256);

internal static class SourceReplayTerminalClampContract
{
    public static bool HasConsistentMeasuredStepRatio(
        IReadOnlyList<double>? capturedOffsets, double measuredStepRatio, int viewportHeight)
    {
        if (capturedOffsets is null || capturedOffsets.Count < 2 || viewportHeight < 1 ||
            !double.IsFinite(measuredStepRatio) || measuredStepRatio <= 0)
        {
            return false;
        }

        double first = capturedOffsets[0];
        double last = capturedOffsets[^1];
        if (!double.IsFinite(first) || !double.IsFinite(last) || last <= first)
            return false;

        // Edge reports the mean of its measured capture deltas. A strictly
        // evidenced terminal clamp shortens that mean even though every
        // preceding movement still follows the native traversal profile.
        double expected = (last - first) / (capturedOffsets.Count - 1);
        return Math.Abs(measuredStepRatio - expected) <= 1.0 / viewportHeight + 0.000001;
    }

    public static bool IsComplete(
        IReadOnlyList<(double CaptureOffset, IReadOnlyList<double> MovementOffsets)> nativeViewports,
        IReadOnlyList<double>? capturedOffsets,
        IReadOnlyList<SourceReplayTerminalClampObservation>? observations,
        int renderedHeight,
        int nativeEstimatedHeight,
        int viewportHeight,
        SourceReplayReadmeIdentity? nativeSourceIdentity = null,
        SourceReplayReadmeIdentity? edgeSourceIdentity = null)
    {
        if (nativeViewports.Count == 0 || capturedOffsets is null ||
            capturedOffsets.Count != nativeViewports.Count || observations is null ||
            viewportHeight < 1 || renderedHeight < viewportHeight ||
            nativeEstimatedHeight < viewportHeight)
        {
            return false;
        }

        double tolerance = 1.0 / viewportHeight + 0.000001;
        double maximumOffset = (renderedHeight - viewportHeight) / (double)viewportHeight;
        int observationIndex = 0;
        bool clamped = false;

        for (int viewportIndex = 0; viewportIndex < nativeViewports.Count; viewportIndex++)
        {
            var native = nativeViewports[viewportIndex];
            if (native.MovementOffsets is null)
                return false;

            for (int movementIndex = 0; movementIndex < native.MovementOffsets.Count; movementIndex++)
            {
                if (!ValidateOffset(native.MovementOffsets[movementIndex], viewportIndex,
                        movementIndex, null))
                {
                    return false;
                }
            }

            if (!ValidateOffset(native.CaptureOffset, viewportIndex, -1,
                    capturedOffsets[viewportIndex]))
            {
                return false;
            }

            if (viewportIndex > 0)
            {
                double actualStep = capturedOffsets[viewportIndex] -
                    capturedOffsets[viewportIndex - 1];
                if (!double.IsFinite(actualStep) || actualStep < -tolerance ||
                    actualStep > 0.90 + 2 * tolerance ||
                    (!clamped && actualStep <= 0))
                {
                    return false;
                }
            }
        }

        if (observationIndex != observations.Count)
            return false;

        if (!clamped)
            return true;

        if (!MatchesExactSource(nativeSourceIdentity, edgeSourceIdentity))
            return false;

        // A shorter Edge page can reach its physical bottom before the native
        // profile does. This is only a terminal suffix, not a general movement
        // tolerance or a waiver of the existing full-page extent fidelity gate.
        double extentRatio = nativeEstimatedHeight / (double)renderedHeight;
        return extentRatio is >= 0.90 and <= 1.10;

        bool ValidateOffset(double requested, int viewportIndex, int movementIndex,
            double? captured)
        {
            if (!double.IsFinite(requested) || requested < 0 ||
                (captured is double actual && (!double.IsFinite(actual) || actual < 0)))
            {
                return false;
            }

            bool shouldClamp = requested > maximumOffset + tolerance;
            if (!shouldClamp)
            {
                // Once Edge has reached the bottom, a later nonterminal target
                // would mean this was not a contiguous terminal suffix.
                return !clamped &&
                    (captured is null || Math.Abs(captured.Value - requested) <= tolerance);
            }

            if (observationIndex >= observations.Count)
            {
                return false;
            }

            SourceReplayTerminalClampObservation observation = observations[observationIndex++];
            if (observation.ViewportIndex != viewportIndex ||
                observation.MovementIndex != movementIndex ||
                observation.DocumentHeightPx != renderedHeight ||
                observation.ViewportHeightPx != viewportHeight ||
                !double.IsFinite(observation.RequestedOffsetViewportUnits) ||
                !double.IsFinite(observation.ActualOffsetViewportUnits) ||
                !double.IsFinite(observation.MaximumOffsetViewportUnits) ||
                Math.Abs(observation.RequestedOffsetViewportUnits - requested) > tolerance ||
                Math.Abs(observation.ActualOffsetViewportUnits - maximumOffset) > tolerance ||
                Math.Abs(observation.MaximumOffsetViewportUnits - maximumOffset) > tolerance ||
                (captured is double captureActual &&
                    Math.Abs(captureActual - observation.ActualOffsetViewportUnits) > tolerance))
            {
                return false;
            }

            clamped = true;
            return true;
        }
    }

    private static bool MatchesExactSource(
        SourceReplayReadmeIdentity? native,
        SourceReplayReadmeIdentity? edge)
        => native is not null && edge is not null &&
           IsHex(native.ReadmeGitBlobSha1, 40) &&
           IsHex(edge.ReadmeGitBlobSha1, 40) &&
           string.Equals(native.ReadmeGitBlobSha1, edge.ReadmeGitBlobSha1,
               StringComparison.OrdinalIgnoreCase) &&
           IsHex(native.ReadmeSha256, 64) &&
           IsHex(edge.ReadmeSha256, 64) &&
           string.Equals(native.ReadmeSha256, edge.ReadmeSha256,
               StringComparison.OrdinalIgnoreCase) &&
           native.ByteSize > 0 && native.ByteSize == edge.ByteSize &&
           IsHex(native.DecodedTextSha256, 64) &&
           IsHex(edge.DecodedTextSha256, 64) &&
           string.Equals(native.DecodedTextSha256, edge.DecodedTextSha256,
               StringComparison.OrdinalIgnoreCase);

    private static bool IsHex(string? value, int length)
        => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}
