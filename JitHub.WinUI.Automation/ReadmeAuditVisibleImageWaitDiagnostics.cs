using System.Globalization;
using System.Text;

internal readonly record struct ReadmeAuditVisibleImageTimeoutSnapshot(
    string? HostItemStatus,
    bool HostItemStatusRead,
    int VisibleImageCount,
    int LoadingVisibleImageCount,
    bool ImageScanComplete,
    string Viewport,
    string Scroll);

internal static class ReadmeAuditVisibleImageWaitDiagnostics
{
    internal const int MaximumHostItemStatusCharacters = 256;
    private const int MaximumPhaseCharacters = 64;

    internal static string FormatFailure(
        string phase,
        int? viewportIndex,
        ReadmeAuditVisibleImageTimeoutSnapshot snapshot)
    {
        string boundedPhase = phase.Length <= MaximumPhaseCharacters
            ? phase
            : phase[..MaximumPhaseCharacters];
        string itemStatus = snapshot.HostItemStatusRead
            ? FormatBoundedStatus(snapshot.HostItemStatus)
            : "<deadline UIA read failed>; lastPoll=" + FormatBoundedStatus(snapshot.HostItemStatus);
        string visibleImages = FormatCount(snapshot.VisibleImageCount);
        string loadingImages = FormatCount(snapshot.LoadingVisibleImageCount);
        string scanStatus = snapshot.ImageScanComplete ? "complete" : "incomplete";
        string viewport = viewportIndex is int index
            ? index.ToString(CultureInfo.InvariantCulture)
            : "unspecified";

        return $"[visible-image-wait phase={boundedPhase} viewport={viewport} " +
            $"hostItemStatus={itemStatus} visibleImages={visibleImages} " +
            $"loadingVisibleImages={loadingImages} unavailableVisibleImages=not-exposed-by-UIA " +
            $"imageScan={scanStatus} hostBounds=({snapshot.Viewport}) scroll=({snapshot.Scroll})].";
    }

    private static string FormatBoundedStatus(string? status)
    {
        if (status is null)
            return "<null>";
        if (status.Length == 0)
            return "<empty>";

        int length = Math.Min(status.Length, MaximumHostItemStatusCharacters);
        var builder = new StringBuilder((MaximumHostItemStatusCharacters * 6) + 24);
        builder.Append('\'');
        for (int index = 0; index < length; index++)
        {
            char character = status[index];
            if (character is '\\' or '\'')
            {
                builder.Append('\\').Append(character);
            }
            else if (char.IsControl(character))
            {
                builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }
        builder.Append('\'');
        if (status.Length > length)
            builder.Append("…<truncated>");
        return builder.ToString();
    }

    private static string FormatCount(int count) =>
        count >= 0 ? count.ToString(CultureInfo.InvariantCulture) : "unknown";
}
