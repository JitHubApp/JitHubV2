using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeAuditVisibleImageWaitDiagnosticsTests
{
    [Fact]
    public void FormatsTheActualAggregateStatusAndViewportState()
    {
        var snapshot = new ReadmeAuditVisibleImageTimeoutSnapshot(
            "Loading image: README",
            HostItemStatusRead: true,
            VisibleImageCount: 4,
            LoadingVisibleImageCount: 2,
            ImageScanComplete: true,
            Viewport: "x=12,y=34,width=800,height=600",
            Scroll: "percent=25.000,viewSize=40.000");

        string diagnostic = ReadmeAuditVisibleImageWaitDiagnostics.FormatFailure(
            "traversal-viewport",
            7,
            snapshot);

        Assert.Contains("phase=traversal-viewport viewport=7", diagnostic);
        Assert.Contains("hostItemStatus='Loading image: README'", diagnostic);
        Assert.Contains("visibleImages=4 loadingVisibleImages=2", diagnostic);
        Assert.Contains("unavailableVisibleImages=not-exposed-by-UIA", diagnostic);
        Assert.Contains("hostBounds=(x=12,y=34,width=800,height=600)", diagnostic);
        Assert.Contains("scroll=(percent=25.000,viewSize=40.000)", diagnostic);
    }

    [Fact]
    public void TruncatesAndEscapesHostStatusAndMarksUnavailableCountsAsUnknown()
    {
        int statusPrefixLength = ReadmeAuditVisibleImageWaitDiagnostics.MaximumHostItemStatusCharacters - 6;
        string status = new string('x', statusPrefixLength) +
            "\r\n'\\\0znot-included";
        string phase = "phase-name-that-is-longer-than-the-allowed-diagnostic-phase-boundary-and-is-truncated";
        var snapshot = new ReadmeAuditVisibleImageTimeoutSnapshot(
            status,
            HostItemStatusRead: true,
            VisibleImageCount: -1,
            LoadingVisibleImageCount: -1,
            ImageScanComplete: false,
            Viewport: "unavailable",
            Scroll: "unavailable");

        string diagnostic = ReadmeAuditVisibleImageWaitDiagnostics.FormatFailure(
            phase,
            viewportIndex: null,
            snapshot: snapshot);

        Assert.Contains(new string('x', statusPrefixLength), diagnostic);
        Assert.Contains("\\u000D\\u000A", diagnostic);
        Assert.Contains("\\'", diagnostic);
        Assert.Contains("\\\\", diagnostic);
        Assert.Contains("\\u0000", diagnostic);
        Assert.Contains("…<truncated>", diagnostic);
        Assert.DoesNotContain("not-included", diagnostic);
        Assert.Contains($"phase={phase[..64]} viewport=unspecified", diagnostic);
        Assert.Contains("viewport=unspecified", diagnostic);
        Assert.Contains("visibleImages=unknown loadingVisibleImages=unknown", diagnostic);
        Assert.Contains("imageScan=incomplete", diagnostic);
        Assert.True(diagnostic.Length < 2_000);
    }

    [Fact]
    public void ReportsLastPolledStatusWhenDeadlineStatusReadFails()
    {
        var snapshot = new ReadmeAuditVisibleImageTimeoutSnapshot(
            "Loading embedded content",
            HostItemStatusRead: false,
            VisibleImageCount: 0,
            LoadingVisibleImageCount: 0,
            ImageScanComplete: true,
            Viewport: "invalid",
            Scroll: "unavailable");

        string diagnostic = ReadmeAuditVisibleImageWaitDiagnostics.FormatFailure(
            "first-viewport",
            0,
            snapshot);

        Assert.Contains("hostItemStatus=<deadline UIA read failed>; lastPoll='Loading embedded content'", diagnostic);
    }
}
