using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-order contracts for the control's XAML/Win2D publication boundary.
/// These do not substitute for live resize, navigation, and unload validation.
/// </summary>
public sealed class SelectionAdornerSurfaceLifetimeContractTests
{
    [Fact]
    public void DrawingSessionIsClosedBeforePublishingGeometryOrMutatingHandles()
    {
        string body = MethodBody(ReadControl(), "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        const string scopedSession = "using (var drawingSession = CanvasComposition.CreateDrawingSession(source.Surface,";
        int session = body.IndexOf(scopedSession, StringComparison.Ordinal);
        Assert.True(session >= 0, "The surface draw must use an explicit, bounded using block.");
        int close = BlockEnd(body, body.IndexOf('{', session));
        int commit = body.IndexOf("CommitSelectionAdornerGeometry(paintRects, paintBackgrounds);", close, StringComparison.Ordinal);
        int viewport = body.IndexOf("CommitSelectionAdornerViewport(", close, StringComparison.Ordinal);
        int handles = body.IndexOf("UpdateSelectionHandlesUnderPaint(", close, StringComparison.Ordinal);
        int validation = body.IndexOf("surfaceVersion != _selectionAdornerSurfaces.Generation", close, StringComparison.Ordinal);

        Assert.True(validation > close && commit > validation && viewport > commit && handles > viewport);
        Assert.Contains("requestVersion != _selectionAdornerRequestVersion", body[close..commit], StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(source, _selectionAdornerSurfaces.Surface)", body[close..commit], StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision != layoutRevision", body[close..commit], StringComparison.Ordinal);
        Assert.DoesNotContain("CommitSelectionAdorner", body[session..close], StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSelectionHandles", body[session..close], StringComparison.Ordinal);
        Assert.Contains("GC.KeepAlive(source);", body[close..], StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementPublishesOnlyAfterAttachmentAndRevalidatesRetirement()
    {
        // Ownership moved into SelectionAdornerSurfaceLifetime; the behavioral
        // reentrancy/failure cases are in SelectionAdornerSurfaceLifetimeTests.
        string body = MethodBody(ReadLifetime(), "internal TSurface? Ensure(");
        int deferred = body.IndexOf("if (_mutating)", StringComparison.Ordinal);
        int create = body.IndexOf("TSurface replacement = _create(device, allocation);", StringComparison.Ordinal);
        int retain = body.IndexOf("TSurface? previous = _surface;", StringComparison.Ordinal);
        int attach = body.IndexOf("SetSource(host, replacement);", StringComparison.Ordinal);
        int publish = body.IndexOf("_surface = replacement;", StringComparison.Ordinal);
        int retire = body.IndexOf("Retire(previous);", StringComparison.Ordinal);

        Assert.True(deferred >= 0 && create > deferred && retain > create && attach > retain && publish > attach && retire > publish);
        Assert.Contains("Retire(replacement);", body[attach..publish], StringComparison.Ordinal);
        Assert.Contains("CompleteDeferredRelease(host)", body[retire..], StringComparison.Ordinal);

        string control = MethodBody(ReadControl(), "private SelectionAdornerCompositionSurface? EnsureSelectionAdornerSurface(");
        int ensure = control.IndexOf("_selectionAdornerSurfaces.Ensure(", StringComparison.Ordinal);
        Assert.True(ensure >= 0);
        Assert.Contains("!ReferenceEquals(host, _selectionAdornerHost)", control[ensure..], StringComparison.Ordinal);
        Assert.Contains("_isDisposed || _isUnloaded", control[ensure..], StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseKeepsPriorOwnerUntilXamlDetachesAndRejectsReentrantReset()
    {
        string body = MethodBody(ReadLifetime(), "internal bool Release(");
        int deferred = body.IndexOf("_releaseRequested = true;", StringComparison.Ordinal);
        int retain = body.IndexOf("TSurface? previous = _surface;", StringComparison.Ordinal);
        int clear = body.IndexOf("_surface = null;", StringComparison.Ordinal);
        int retire = body.IndexOf("Retire(previous);", StringComparison.Ordinal);
        int detach = body.IndexOf("SetSource(target, null);", StringComparison.Ordinal);

        Assert.True(deferred >= 0 && retain > deferred && clear > retain && retire > clear && detach > retire);
        Assert.Contains("_reportReleaseFailure?.Invoke(failure);", body[detach..], StringComparison.Ordinal);
        Assert.Contains("finally", body[detach..], StringComparison.Ordinal);

        string control = MethodBody(ReadControl(), "private void ReleaseSelectionAdornerSurface()");
        Assert.Contains("_selectionAdornerSurfaces.Release(_selectionAdornerHost);", control, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotCommitInvalidatesButNeverReleasesOrDetachesTheSelectionAdorner()
    {
        // P1-032: every commit used to discard the attached CanvasImageSource
        // and remove its Image from the tree, leaving final release of each
        // replaced surface to XAML image-peer cleanup.
        string body = MethodBody(ReadControl(), "private async Task RebuildInternalAsync(");
        int publish = body.IndexOf("_snapshot = snapshot;", StringComparison.Ordinal);
        Assert.True(publish >= 0);
        string afterPublish = body[publish..];

        Assert.DoesNotMatch(@"(?<![A-Za-z])ReleaseSelectionAdornerSurface\(\);", afterPublish);
        Assert.DoesNotContain("_overlay.Children.Clear();", afterPublish, StringComparison.Ordinal);
        int invalidate = afterPublish.IndexOf("InvalidateSelectionAdornerPresentation();", StringComparison.Ordinal);
        int prune = afterPublish.IndexOf("RemoveOverlayChildrenExceptSelectionAdorner();", StringComparison.Ordinal);
        Assert.True(invalidate >= 0 && prune > invalidate);

        string pruneBody = MethodBody(ReadControl(), "private void RemoveOverlayChildrenExceptSelectionAdorner()");
        Assert.Contains("ReferenceEquals(", pruneBody, StringComparison.Ordinal);
        Assert.Contains("_selectionAdorner", pruneBody, StringComparison.Ordinal);
        Assert.Contains("RemoveAt(", pruneBody, StringComparison.Ordinal);
    }

    [Fact]
    public void UnloadSurfaceReleaseCannotAbortTheRemainingOneShotCleanup()
    {
        string body = MethodBody(ReadControl(), "private void ReleaseLoadedResources()");
        Assert.DoesNotMatch(@"(?<![A-Za-z])ReleaseSelectionAdornerSurface\(\);", body);
        int release = body.IndexOf("TryReleaseSelectionAdornerSurface();", StringComparison.Ordinal);
        int clear = body.IndexOf("_overlay?.Children.Clear();", StringComparison.Ordinal);
        int retire = body.IndexOf("RetireSnapshot(snap);", StringComparison.Ordinal);
        Assert.True(release >= 0 && clear > release && retire > clear);

        string tryRelease = MethodBody(ReadControl(), "private bool TryReleaseSelectionAdornerSurface()");
        Assert.Contains("catch (Exception", tryRelease, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLossCatchBlocksDeferSurfaceReleaseToTheGuardedRecoveryHandler()
    {
        string source = ReadControl();
        foreach (string signature in new[]
        {
            "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(",
            "private SelectionAdornerCompositionSurface? EnsureSelectionAdornerSurface(",
        })
        {
            string body = MethodBody(source, signature);
            Assert.DoesNotMatch(@"ReleaseSelectionAdornerSurface\(\);\s*HandleCanvasDeviceLost\(", body);
        }

        string refresh = MethodBody(source, "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        System.Text.RegularExpressions.Match guarded = System.Text.RegularExpressions.Regex.Match(refresh, @"\btry\s*\{");
        int apply = refresh.IndexOf("ApplySelectionAdornerViewportUnderPaint(", StringComparison.Ordinal);
        Assert.True(guarded.Success && apply > guarded.Index,
            "Surface creation/attachment must run inside the device-loss guarded region.");
    }

    [Fact]
    public void DeviceLossNotifiesTheFailedWin2DDevicesAndPurgesTheLayoutDeviceCache()
    {
        string body = MethodBody(ReadControl(), "private void HandleCanvasDeviceLost(int failureHResult)");
        Assert.Contains("_snapshotLayoutDevice", body, StringComparison.Ordinal);
        Assert.Contains("NotifyLostCanvasDevices(", body, StringComparison.Ordinal);

        string notify = MethodBody(ReadControl(), "private static void NotifyLostCanvasDevices(");
        Assert.Contains(".IsDeviceLost(failureHResult)", notify, StringComparison.Ordinal);
        Assert.Contains(".RaiseDeviceLost()", notify, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionPaintIsGatedOnTheSnapshotLayoutDevice()
    {
        string refresh = MethodBody(ReadControl(), "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        int gate = refresh.IndexOf("ReferenceEquals(_snapshotLayoutDevice,", StringComparison.Ordinal);
        int draw = refresh.IndexOf(".CreateDrawingSession(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && draw > gate,
            "Snapshot bitmaps must never be drawn into a surface created on a different Win2D device.");
    }

    private static string ReadControl() => ReadControlsSource("MarkdownRendererControl.cs");

    private static string ReadLifetime() => ReadControlsSource("SelectionAdornerSurfaceLifetime.cs");

    private static string ReadControlsSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        Assert.True(open >= 0);
        return source[open..(BlockEnd(source, open) + 1)];
    }

    private static int BlockEnd(string source, int open)
    {
        Assert.True(open >= 0);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return index;
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
