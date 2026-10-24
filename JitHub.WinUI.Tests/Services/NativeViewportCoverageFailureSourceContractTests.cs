using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NativeViewportCoverageFailureSourceContractTests
{
    [Fact]
    public void InvalidCaptureProfileIsPreservedWithoutWaivingCoverageFailure()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory!.FullName,
            "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));
        int check = source.IndexOf("EnsureNativeViewportCoverage(tiles);", StringComparison.Ordinal);
        int record = source.IndexOf("WriteNativeViewportCoverageFailure(output, tiles);", check, StringComparison.Ordinal);
        Assert.True(check >= 0 && record > check);
        Assert.Contains("catch (InvalidOperationException)", source[check..record], StringComparison.Ordinal);
        Assert.StartsWith("WriteNativeViewportCoverageFailure(output, tiles);\n            throw;",
            source[record..].Replace("\r\n", "\n"));
        int helper = source.IndexOf("private static void WriteNativeViewportCoverageFailure(", StringComparison.Ordinal);
        int validator = source.IndexOf("private static void EnsureNativeViewportCoverage(", helper, StringComparison.Ordinal);
        string body = source[helper..validator];
        Assert.Contains("native-viewport-coverage-failure.json", body, StringComparison.Ordinal);
        Assert.Contains("tiles.Take(MaximumTraversalViewports)", body, StringComparison.Ordinal);
        Assert.Contains("tile.RendererDocumentTopDip", body, StringComparison.Ordinal);
        Assert.Contains("tile.ScrollTopViewportUnits", body, StringComparison.Ordinal);
        Assert.Contains("scrollPercent = double.IsFinite(tile.ScrollPercent)", body, StringComparison.Ordinal);
        Assert.Contains("verticalViewSize = double.IsFinite(tile.VerticalViewSize)", body, StringComparison.Ordinal);
        Assert.Contains("captureViewportTrace = tile.CaptureViewportTrace", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Uri", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureTraceIsFailureOnlyAndAllSerializedMeasurementsAreFiniteOrNull()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory!.FullName,
            "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));

        Assert.Contains("[System.Text.Json.Serialization.JsonIgnore]\n    public NativeCaptureViewportTrace? CaptureViewportTrace",
            source.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("FiniteOrNull(warmCapture.DocumentTop)", source, StringComparison.Ordinal);
        Assert.Contains("FiniteOrNull(capture.DocumentTop)", source, StringComparison.Ordinal);
        Assert.Contains("double? WarmRendererDocumentTopDip", source, StringComparison.Ordinal);
        Assert.Contains("double? SavedRendererDocumentTopDip", source, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite(value) ? value : null", source, StringComparison.Ordinal);
        Assert.Contains("captureViewportTrace = tile.CaptureViewportTrace", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureFailureReceiptRecordsRawUiAAndRendererPositionsAroundBothPaints()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory!.FullName,
            "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));

        int captureStart = source.IndexOf("private static (int Width, int Height) CaptureHost(", StringComparison.Ordinal);
        int scrollReaderStart = source.IndexOf("private static NativeScrollPositionDiagnostic ReadNativeScrollPosition(", captureStart, StringComparison.Ordinal);
        Assert.True(captureStart >= 0 && scrollReaderStart > captureStart);
        string capture = source[captureStart..scrollReaderStart];

        int beforeWarm = capture.IndexOf("beforeWarmCapture = ReadNativeScrollPosition(host)", StringComparison.Ordinal);
        int warmPaint = capture.IndexOf("RendererCaptureResponse warmCapture = RequestRendererCapture(", StringComparison.Ordinal);
        int afterWarm = capture.IndexOf("afterWarmCapture = ReadNativeScrollPosition(host)", StringComparison.Ordinal);
        int imageWait = capture.IndexOf("WaitForVisibleImages(", StringComparison.Ordinal);
        int beforeSaved = capture.IndexOf("beforeSavedCapture = ReadNativeScrollPosition(host)", StringComparison.Ordinal);
        int savedPaint = capture.IndexOf("RendererCaptureResponse capture = RequestRendererCapture(", StringComparison.Ordinal);
        int afterSaved = capture.IndexOf("afterSavedCapture = ReadNativeScrollPosition(host)", StringComparison.Ordinal);
        Assert.True(beforeWarm >= 0 && beforeWarm < warmPaint);
        Assert.True(warmPaint < afterWarm && afterWarm < imageWait);
        Assert.True(imageWait < beforeSaved && beforeSaved < savedPaint);
        Assert.True(savedPaint < afterSaved);
        Assert.Contains("warmCapture.DocumentTop", capture, StringComparison.Ordinal);
        Assert.Contains("capture.DocumentTop", capture, StringComparison.Ordinal);

        int readerEnd = source.IndexOf("private static RendererCaptureResponse RequestRendererCapture(", scrollReaderStart, StringComparison.Ordinal);
        string reader = source[scrollReaderStart..readerEnd];
        Assert.Contains("VerticalScrollPercent.ValueOrDefault", reader, StringComparison.Ordinal);
        Assert.Contains("VerticalViewSize.ValueOrDefault", reader, StringComparison.Ordinal);
        Assert.Contains("ReadFailureType: null", reader, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", reader, StringComparison.Ordinal);
        Assert.Contains("exception.GetType().Name", reader, StringComparison.Ordinal);
    }
}
