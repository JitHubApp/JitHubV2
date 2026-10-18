using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class TouchContactObservationSourceContractTests
{
    [Fact]
    public void SampleObserverReadsRoutedTouchEventsWithoutChangingInputOrSelection()
    {
        string root = FindRepositoryRoot();
        string observer = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.Sample",
            "TouchContactObservation.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string window = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer.Sample",
            "MainWindow.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("UIElement.PointerPressedEvent, _pressedHandler, handledEventsToo: true", observer, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerMovedEvent, _movedHandler, handledEventsToo: true", observer, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerReleasedEvent, _releasedHandler, handledEventsToo: true", observer, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerCanceledEvent, _canceledHandler, handledEventsToo: true", observer, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerCaptureLostEvent, _captureLostHandler, handledEventsToo: true", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.Loaded += OnRendererLoaded", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.Unloaded += OnRendererUnloaded", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.RenderCompleted += OnRendererRenderCompleted", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.RenderCompleted -= OnRendererRenderCompleted", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.RemoveHandler(UIElement.PointerCaptureLostEvent, _captureLostHandler)", observer, StringComparison.Ordinal);
        Assert.Contains("MaximumRememberedPointers = 16", observer, StringComparison.Ordinal);
        Assert.Contains("MaximumVisualNodesToInspect = 4096", observer, StringComparison.Ordinal);
        Assert.Contains("if (_pointers.Count >= MaximumRememberedPointers)", observer, StringComparison.Ordinal);
        Assert.Contains("if (!TryGetOrCreatePointer(pointerId, out PointerObservation pointer))", observer, StringComparison.Ordinal);
        Assert.Contains("pointer = null!;\n                return false;", observer, StringComparison.Ordinal);
        Assert.Contains("var currentPoint = args.GetCurrentPoint(renderer)", observer, StringComparison.Ordinal);
        Assert.Contains("PointerEventKind routedKind = kind", observer, StringComparison.Ordinal);
        Assert.Contains("currentPoint.Properties.IsCanceled", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastRoutedEvent = routedKind", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastNativeCanceled = nativeCanceled", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.RawReleaseCount = Increment(pointer.RawReleaseCount)", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.NativeCanceledPacketCount = Increment(pointer.NativeCanceledPacketCount)", observer, StringComparison.Ordinal);
        Assert.Contains("XamlRoot xamlRoot = renderer.XamlRoot", observer, StringComparison.Ordinal);
        Assert.Contains("xamlRoot.Content as UIElement", observer, StringComparison.Ordinal);
        Assert.Contains("args.GetCurrentPoint(rootContent).Position", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastPositionXDip = currentPosition.X", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastPositionYDip = currentPosition.Y", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastRootPositionXDip = rootPosition.X", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.LastRootPositionYDip = rootPosition.Y", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.EventRasterizationScale = rasterizationScale", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"lastPositionDipX\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"lastPositionDipY\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"lastPositionRootDipX\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"lastPositionRootDipY\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"eventRasterizationScale\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"lastEventSequence\\\"", observer, StringComparison.Ordinal);
        Assert.Contains("pointer.InContact = currentPoint.IsInContact", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("Properties.IsInContact", observer, StringComparison.Ordinal);
        Assert.Contains("ScheduleLayoutCapture(renderer)", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low", observer, StringComparison.Ordinal);
        Assert.Contains("kind is PointerEventKind.Pressed or PointerEventKind.Released or", observer, StringComparison.Ordinal);
        Assert.Contains("PointerEventKind.Canceled or PointerEventKind.CaptureLost", observer, StringComparison.Ordinal);
        Assert.Contains("rendererLocalCornersDip", observer, StringComparison.Ordinal);
        Assert.Contains("actualWidthPhysicalPx", observer, StringComparison.Ordinal);
        Assert.Contains("missingXamlRootOrScale", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("RasterizationScale ?? 1", observer, StringComparison.Ordinal);
        Assert.Contains("peerBoundsRaw", observer, StringComparison.Ordinal);
        Assert.Contains("nearestCanvas", observer, StringComparison.Ordinal);
        Assert.Contains("visualTreeSearchTruncated", observer, StringComparison.Ordinal);
        Assert.Contains("rendererActualWidthDip", observer, StringComparison.Ordinal);
        Assert.Contains("snapshot.Size", observer, StringComparison.Ordinal);
        Assert.Contains("snapshotSizeDip", observer, StringComparison.Ordinal);
        Assert.Contains("ownedScrollMetrics", observer, StringComparison.Ordinal);
        Assert.Contains("renderer.TryGetAutomationScrollMetrics", observer, StringComparison.Ordinal);
        Assert.Contains("FrameworkElementAutomationPeer.FromElement(handle)", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameworkElementAutomationPeer.CreatePeerForElement", observer, StringComparison.Ordinal);
        Assert.Contains("_pendingLayoutCaptureRequest != 0", observer, StringComparison.Ordinal);
        Assert.Contains("generation != _rendererGeneration || !ReferenceEquals(renderer, _renderer)", observer, StringComparison.Ordinal);
        int movedCase = observer.IndexOf("case PointerEventKind.Moved:", StringComparison.Ordinal);
        int terminalCase = observer.IndexOf("case PointerEventKind.Released:", StringComparison.Ordinal);
        int boundaryCapture = terminalCase < 0 ? -1 : observer.IndexOf("ScheduleLayoutCapture(renderer);", terminalCase, StringComparison.Ordinal);
        Assert.True(movedCase >= 0 && terminalCase > movedCase && boundaryCapture > terminalCase);
        Assert.Contains("#pragma warning disable MR1001", observer, StringComparison.Ordinal);
        Assert.Contains("#pragma warning restore MR1001", observer, StringComparison.Ordinal);
        Assert.Contains("_observationErrors = Increment(_observationErrors);", observer, StringComparison.Ordinal);
        Assert.Contains("PublishSnapshot();", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"observationErrors\\\":", observer, StringComparison.Ordinal);
        Assert.Contains("\\\"observationStatus\\\":\\\"", observer, StringComparison.Ordinal);

        int observationCatch = observer.IndexOf("catch (Exception)", StringComparison.Ordinal);
        Assert.True(observationCatch >= 0);
        int caughtFailureCount = observer.IndexOf("_observationErrors = Increment(_observationErrors);", observationCatch, StringComparison.Ordinal);
        Assert.True(caughtFailureCount > observationCatch);
        int caughtFailurePublish = observer.IndexOf("PublishSnapshot();", caughtFailureCount, StringComparison.Ordinal);
        Assert.True(caughtFailurePublish > caughtFailureCount);

        Assert.DoesNotContain("args.Handled", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer(", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePointerCapture(", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessPointer", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionController", observer, StringComparison.Ordinal);

        Assert.Contains("AutomationProperties.SetAutomationId(_touchContactStatus, \"TouchContactObservation\")", window, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAccessibilityView(_touchContactStatus, AccessibilityView.Raw)", window, StringComparison.Ordinal);
        Assert.Contains("\\\"observationErrors\\\":0,\\\"observationStatus\\\":\\\"ok\\\"", window, StringComparison.Ordinal);
        Assert.Contains("Opacity = 0", window, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservationEnabled = isTouchSelectionPage", window, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservation.Attach(replacement)", window, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservation.Detach()", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_touchContactObservation?.Attach", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_touchContactObservation?.Detach", window, StringComparison.Ordinal);

        int observationCreated = window.IndexOf(
            "_touchContactObservation = new TouchContactObservation(_touchContactStatus)",
            StringComparison.Ordinal);
        int closedRegistered = window.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        Assert.True(observationCreated >= 0 && closedRegistered > observationCreated);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the JitHub repository root.");
    }
}
