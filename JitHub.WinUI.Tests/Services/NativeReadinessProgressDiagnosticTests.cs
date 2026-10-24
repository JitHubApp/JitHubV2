using System.Text;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NativeReadinessProgressDiagnosticTests
{
    [Fact]
    public void SummaryUsesOnlyBoundedAllowlistedProgressFields()
    {
        const string row = "{\"Stage\":\"polling\",\"Reason\":\"waiting-for-viewport-coverage\"," +
            "\"Generation\":11,\"CurrentGeneration\":11,\"PublishedGeneration\":11," +
            "\"SnapshotGeneration\":11,\"LayoutRevision\":24,\"PaintCallbackCount\":3," +
            "\"PaintedRegionCount\":2,\"PaintRequestedGeneration\":11,\"PaintedGeneration\":0," +
            "\"VisibleLoadingImageCount\":1,\"PendingImageRegionCount\":2,\"UncoveredRegionCount\":3," +
            "\"ViewportMeasured\":true,\"HasVisibleLoadingImages\":true," +
            "\"PaintAcknowledgementRecorded\":false,\"PaintAcknowledgementIsCurrent\":false," +
            "\"Title\":\"PRIVATE README TITLE\",\"Url\":\"https://private.invalid/path\"}";

        string summary = NativeReadinessProgressDiagnostic.FormatLastSummary(
            Encoding.UTF8.GetBytes(row + "\n"),
            startsAtFileBeginning: true);

        Assert.Contains("stage=polling reason=waiting-for-viewport-coverage", summary, StringComparison.Ordinal);
        Assert.Contains("generation=11 currentGeneration=11 publishedGeneration=11 snapshotGeneration=11 layoutRevision=24", summary, StringComparison.Ordinal);
        Assert.Contains("paintCallbacks=3 paintedRegions=2 paintRequestedGeneration=11 paintedGeneration=0", summary, StringComparison.Ordinal);
        Assert.Contains("visibleLoadingImages=1 pendingImageRegions=2 uncoveredRegions=3", summary, StringComparison.Ordinal);
        Assert.Contains("viewportMeasured=true hasVisibleLoadingImages=true", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE README TITLE", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private.invalid", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Title", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Url", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTokensAndMalformedCountersAreRedactedRatherThanEchoed()
    {
        const string row = "{\"Stage\":\"user supplied title\",\"Reason\":\"https://private.invalid\"," +
            "\"Generation\":-1,\"CurrentGeneration\":\"17\",\"LayoutRevision\":9223372036854775807}";

        string summary = NativeReadinessProgressDiagnostic.FormatLastSummary(
            Encoding.UTF8.GetBytes(row + "\n"),
            startsAtFileBeginning: true);

        Assert.Contains("stage=unknown-token reason=unknown-token", summary, StringComparison.Ordinal);
        Assert.Contains("generation=n/a currentGeneration=n/a", summary, StringComparison.Ordinal);
        Assert.Contains("layoutRevision=9223372036854775807", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("user supplied title", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private.invalid", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedTailKeepsLastCompleteValidRowAndIgnoresPartialSuffixAndPrefix()
    {
        const string prior = "{\"Stage\":\"polling\",\"Reason\":\"waiting-for-paint\",\"Generation\":4}";
        const string last = "{\"Stage\":\"polling\",\"Reason\":\"visible-images-loading\",\"Generation\":5}";
        byte[] tail = Encoding.UTF8.GetBytes("partial-prefix\n" + prior + "\n" + last + "\n{\"Stage\":");

        string summary = NativeReadinessProgressDiagnostic.FormatLastSummary(
            tail,
            startsAtFileBeginning: false);

        Assert.Contains("reason=visible-images-loading generation=5", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting-for-paint", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("partial-prefix", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FileReaderUsesTheBoundedTailAndFindsTheNewestCompleteProgressRow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"readiness-progress-{Guid.NewGuid():N}.ndjson");
        const string last = "{\"Stage\":\"polling\",\"Reason\":\"visible-deferred-scene-pending-or-unverified\",\"Generation\":29}";
        try
        {
            File.WriteAllText(path, new string('x', 64 * 1024) + "\n" + last + "\n");

            string summary = NativeReadinessProgressDiagnostic.ReadLastSummary(path);

            Assert.Contains("reason=visible-deferred-scene-pending-or-unverified generation=29", summary, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 32), summary, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
