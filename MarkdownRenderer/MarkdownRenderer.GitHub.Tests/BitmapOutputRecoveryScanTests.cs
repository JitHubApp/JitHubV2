using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BitmapOutputRecoveryScanTests
{
    [Fact]
    public void IdleScanDoesNoWork()
    {
        var scan = new BitmapOutputRecoveryScan();
        Assert.False(scan.TryTake(500, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(500)]
    public void EveryOrdinalIsVisitedBeforeRecoveryStops(int count)
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            Assert.True(scan.TryTake(count, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: false);
            Assert.Equal(ordinal != count - 1, scan.IsPending);
        }
        Assert.False(scan.TryTake(count, out _));
    }

    [Fact]
    public void RegisteringOrFailedRequestKeepsAnotherCompleteCycleAlive()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < 65; ordinal++)
        {
            Assert.True(scan.TryTake(65, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: ordinal == 0);
        }
        Assert.True(scan.IsPending);
        for (int ordinal = 0; ordinal < 65; ordinal++)
        {
            Assert.True(scan.TryTake(65, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: false);
        }
        Assert.False(scan.IsPending);
    }

    [Fact]
    public void NewSignalDoesNotStarveTailAndKeepsAlreadyVisitedOrdinalAlive()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < 100; ordinal++)
        {
            Assert.True(scan.TryTake(100, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Request();
            scan.Complete(needsRecovery: false);
        }
        Assert.True(scan.IsPending);
        Assert.True(scan.TryTake(100, out int first));
        Assert.Equal(0, first);
    }

    [Fact]
    public void ReplacementResetDoesNotRetainOldCursorOrPendingState()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        Assert.True(scan.TryTake(500, out _));
        scan.Complete(needsRecovery: true);
        scan.Reset();
        Assert.False(scan.IsPending);
        scan.Request();
        Assert.True(scan.TryTake(1, out int first));
        Assert.Equal(0, first);
        scan.Complete(needsRecovery: false);
        Assert.False(scan.IsPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyPublicationEndsRecovery(int count)
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        Assert.False(scan.TryTake(count, out _));
        Assert.False(scan.IsPending);
    }

    [Fact]
    public void TimerAndEventWiringAreBoundedAndDoNotPaintOrRebuild_SourceContractOnly()
    {
        string recovery = ReadSource("MarkdownRendererControl.BitmapRecovery.cs");
        Assert.Contains("System.Math.Min(BitmapOutputRecoveryScan.MaximumProbesPerTick, plans.Count)", recovery);
        Assert.Contains("ObserveBitmapOutputRecovery(isVisible: false)", recovery);
        Assert.Contains("epoch != _bitmapOutputRecoveryEpoch", recovery);
        Assert.Contains("!ReferenceEquals(plans, _imagePlans)", recovery);
        foreach (string forbidden in new[] { "EnsureLoading(", "InvalidateCanvas(", "QueueImageRelayout(",
                     "RequestRebuild(", "CreateTimer(", "Measure(" })
        {
            Assert.DoesNotContain(forbidden, recovery);
        }
        string control = ReadSource("MarkdownRendererControl.cs");
        int apply = control.IndexOf("private void ApplyImageLoadCompleted(", StringComparison.Ordinal);
        int recoveryBranch = control.IndexOf("if (bitmapOutputRecoverySignal)", apply, StringComparison.Ordinal);
        int uia = control.IndexOf(".NotifyImageStatusChanged(completedImage);", apply, StringComparison.Ordinal);
        Assert.True(apply >= 0 && recoveryBranch > apply && uia > recoveryBranch);
        Assert.Contains("control.ApplyImageLoadCompleted(completedImage, layoutInvalidated, bitmapOutputRecoverySignal)", control);
        Assert.Contains("control.ProcessBitmapOutputRecovery();", control);
    }

    private static string ReadSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(fileName);
    }
}
